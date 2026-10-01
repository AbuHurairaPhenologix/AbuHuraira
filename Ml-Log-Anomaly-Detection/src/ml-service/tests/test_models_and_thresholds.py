"""Score direction, adapters, threshold selection and TC-05 (high-latency anomaly raises the score)."""

import numpy as np
import pytest

from app.core.feature_schema import FEATURE_NAMES
from app.datasets.validation import feature_matrix
from app.models.adapters import ADAPTERS, create_adapter
from app.training import thresholds as th
from tests.conftest import NORMAL_FEATURES


def vec(**overrides):
    values = {**NORMAL_FEATURES, **overrides}
    return np.array([[float(values[n]) for n in FEATURE_NAMES]])


@pytest.fixture(scope="module")
def normal_train(dataset):
    frame, _ = dataset
    train = frame[(frame["split"] == "train") & (frame["label"] == 0)]
    return feature_matrix(train)


@pytest.mark.parametrize("algorithm", ["isolation_forest", "lof", "ocsvm"])
def test_higher_score_means_more_anomalous(algorithm, config, normal_train):
    cfg = config.models[algorithm]
    adapter = create_adapter(algorithm, cfg["params"], config.seed, cfg["scaling"]).fit(normal_train[:1500])
    typical = adapter.score(vec())[0]
    extreme = adapter.score(vec(avg_duration_ms=2500.0, p95_duration_ms=7000.0, error_rate=0.4, request_count=600))[0]
    assert extreme > typical


def test_random_forest_reference_is_supervised(config, normal_train):
    cfg = config.models["random_forest"]
    adapter = create_adapter("random_forest", cfg["params"], config.seed, "none")
    with pytest.raises(ValueError, match="requires labels"):
        adapter.fit(normal_train)
    assert config.models["random_forest"]["production_eligible"] is False


def test_adapters_reject_wrong_feature_count(config, normal_train):
    adapter = create_adapter("isolation_forest", {"n_estimators": 10}, 1, "none").fit(normal_train[:200])
    with pytest.raises(ValueError, match="ops-v1"):
        adapter.score(np.zeros((1, 7)))
    assert set(ADAPTERS) == {"isolation_forest", "lof", "ocsvm", "random_forest"}


def test_tc05_high_latency_increases_score_for_registered_models(trained, versions):
    from app.core.config import get_settings
    from app.registry.registry import ModelRegistry

    registry = ModelRegistry(get_settings().models_dir)
    for algorithm in ("isolation_forest", "lof", "ocsvm"):
        model = registry.load(versions[algorithm])
        base = model.adapter.score(vec())[0]
        spiked = model.adapter.score(vec(avg_duration_ms=248.4 * 3.2, p95_duration_ms=681.7 * 3.2))[0]
        assert spiked > base, algorithm
        assert spiked >= model.threshold, f"{algorithm}: 3.2x latency spike should exceed the threshold"


def test_confusion_and_operating_point():
    y = np.array([0, 0, 0, 1, 1])
    s = np.array([0.1, 0.2, 0.9, 0.8, 0.3])
    p = th.operating_point(y, s, 0.5, 0.9, 288)
    assert (p.tp, p.fp, p.tn, p.fn) == (1, 1, 2, 1)
    assert p.precision == pytest.approx(0.5) and p.recall == pytest.approx(0.5)
    assert p.fpr == pytest.approx(1 / 3)
    assert p.alerts_per_1000_windows == pytest.approx(400.0)


def test_candidates_are_quantiles_of_normal_scores_only():
    y = np.array([0] * 100 + [1] * 10)
    s = np.concatenate([np.linspace(0, 1, 100), np.full(10, 50.0)])
    table = th.sensitivity_table(y, s, np.array([0.5, 0.99]), 288)
    assert table[0].threshold == pytest.approx(np.quantile(np.linspace(0, 1, 100), 0.5))
    assert all(p.threshold <= 1.0 for p in table)  # anomaly scores (50.0) never define candidates


def test_select_threshold_objectives():
    y = np.array([0] * 90 + [1] * 10)
    rng = np.random.default_rng(1)
    s = np.concatenate([rng.normal(0, 1, 90), rng.normal(2.5, 1, 10)])
    table = th.sensitivity_table(y, s, th.candidate_quantiles(0.5, 0.99, 50), 288)
    best = th.select_threshold(table, "max_f1", 0.01)
    assert best.f1 == max(p.f1 for p in table)
    constrained = th.select_threshold(table, "max_recall_at_fpr", 0.05)
    assert constrained.fpr <= 0.05
    with pytest.raises(ValueError):
        th.select_threshold(table, "accuracy", 0.01)


def test_trained_models_persist_validation_threshold_and_metadata(trained, versions):
    from app.core.config import get_settings
    from app.registry.registry import ModelRegistry

    registry = ModelRegistry(get_settings().models_dir)
    for algorithm, version in versions.items():
        meta = registry.get(version)
        for key in ("modelId", "modelVersion", "algorithm", "featureSchemaVersion", "trainingPeriodStartUtc",
                    "trainingPeriodEndUtc", "randomSeed", "libraryVersions", "parameters", "validationThreshold",
                    "artifactPath", "artifactSha256", "createdAtUtc", "configSha256", "datasetSha256"):
            assert meta.get(key) is not None, (algorithm, key)
        assert meta["featureNames"] == list(FEATURE_NAMES)
        assert np.isfinite(meta["validationThreshold"])
        assert meta["libraryVersions"]["scikit-learn"]
    eligible = {m["algorithm"] for m in registry.list() if m["productionEligible"]}
    assert "random_forest" not in eligible
    assert trained.recommended in {versions["isolation_forest"], versions["lof"], versions["ocsvm"]}
