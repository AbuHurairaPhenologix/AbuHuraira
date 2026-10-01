"""Internal scoring API: TC-01, TC-02, TC-03, TC-05, service authentication, activation and training jobs."""

import time

import pytest

from tests.conftest import NORMAL_FEATURES, auth


@pytest.fixture()
def active(client, versions):
    for v in (versions["ocsvm"], versions["lof"], versions["isolation_forest"]):
        r = client.post(f"/internal/models/{v}/activate", headers=auth("models.admin"))
        assert r.status_code == 200, r.text
    return versions


def body(version, features=None, **extra):
    return {
        "windowId": "5c7151f0-7f65-4d34-bf80-2bfa6507b66c",
        "service": "identity-api",
        "environment": "production",
        "windowStartUtc": "2022-02-15T14:30:00Z",
        "windowEndUtc": "2022-02-15T14:35:00Z",
        "modelVersion": version,
        "schemaVersion": "ops-v1",
        "features": features or dict(NORMAL_FEATURES),
        **extra,
    }


def score(client, payload, scope="anomaly.score"):
    return client.post("/internal/anomaly/score", json=payload, headers=auth(scope))


# ---- TC-01 ---------------------------------------------------------------------------------------------------
def test_tc01_normal_window_returns_score_below_threshold(client, active):
    r = score(client, body(active["ocsvm"]))
    assert r.status_code == 200, r.text
    data = r.json()
    assert data["isAnomaly"] is False
    assert data["score"] < data["threshold"]
    assert data["modelVersion"] == active["ocsvm"]
    assert data["schemaVersion"] == "ops-v1"
    assert len(data["reasons"]) == 8
    assert "root-cause" not in data["reasonSummary"] or "not a root-cause" in data["reasonSummary"]


def test_report_section_4_7_featureSchema_alias_is_accepted(client, active):
    payload = body(active["ocsvm"])
    payload["featureSchema"] = payload.pop("schemaVersion")
    assert score(client, payload).status_code == 200


# ---- TC-02 ---------------------------------------------------------------------------------------------------
@pytest.mark.parametrize(
    "mutate",
    [
        lambda b: b.update(schemaVersion="ops-v2"),
        lambda b: b["features"].pop("endpoint_entropy"),
        lambda b: b["features"].update(password="hunter2"),
        lambda b: b["features"].update(request_count="142"),
        lambda b: b["features"].update(request_count=True),
        lambda b: b["features"].update(request_count=142.5),
        lambda b: b["features"].update(error_rate=1.5),
        lambda b: b["features"].update(avg_duration_ms=-1),
        lambda b: b.update(unexpected="field"),
    ],
    ids=["schema-mismatch", "missing-feature", "unexpected-feature", "string-type", "bool-type", "float-count", "ratio-range", "negative", "extra-top-level"],
)
def test_tc02_invalid_contract_is_rejected(client, active, mutate):
    payload = body(active["ocsvm"])
    mutate(payload)
    r = score(client, payload)
    assert r.status_code == 422, r.text
    assert r.json()["error"]["code"] == "validation_failed"


def test_tc02_malformed_json_is_rejected(client, active):
    r = client.post("/internal/anomaly/score", content=b"{not json", headers={**auth("anomaly.score"), "Content-Type": "application/json"})
    assert r.status_code == 422


# ---- TC-03 ---------------------------------------------------------------------------------------------------
def test_tc03_missing_model_version_is_rejected(client, active):
    payload = body(active["ocsvm"])
    payload.pop("modelVersion")
    assert score(client, payload).status_code == 422


@pytest.mark.parametrize("label", ["../../artifacts/models/x/model.joblib", "C:/temp/evil.joblib", "/etc/passwd"])
def test_tc03_paths_are_never_accepted_as_model_versions(client, active, label):
    assert score(client, body(label)).status_code == 422


def test_tc03_unknown_model_version_is_404(client, active):
    r = score(client, body("ocsvm-ops-v1-unknown"))
    assert r.status_code == 404
    assert r.json()["error"]["code"] == "model_not_registered"


def test_registered_but_inactive_model_is_409(client, versions):
    client.post(f"/internal/models/{versions['random_forest']}/deactivate", headers=auth("models.admin"))
    r = score(client, body(versions["random_forest"]))
    assert r.status_code == 409
    assert r.json()["error"]["code"] == "model_inactive"


# ---- TC-05 ---------------------------------------------------------------------------------------------------
def test_tc05_high_latency_window_scores_higher_and_is_flagged(client, active):
    normal = score(client, body(active["ocsvm"])).json()
    spike = dict(NORMAL_FEATURES, avg_duration_ms=540.2 * 1.5, p95_duration_ms=1320.5 * 1.5)
    flagged = score(client, body(active["ocsvm"], spike)).json()
    assert flagged["score"] > normal["score"]
    assert flagged["isAnomaly"] is True
    assert "p95 response time" in flagged["reasonSummary"] or "average response time" in flagged["reasonSummary"]


def test_report_example_window_is_flagged(client, active):
    example = {"request_count": 178, "error_rate": 0.16, "avg_duration_ms": 540.2, "p95_duration_ms": 1320.5,
               "auth_failure_rate": 0.02, "dependency_failure_count": 3, "retry_count": 5, "endpoint_entropy": 1.43}
    data = score(client, body(active["ocsvm"], example)).json()
    assert data["isAnomaly"] is True
    assert data["score"] >= data["threshold"]


# ---- batch ---------------------------------------------------------------------------------------------------
def test_batch_scoring(client, active):
    items = [
        {"windowId": "w1", "service": "s", "environment": "production", "features": dict(NORMAL_FEATURES)},
        {"windowId": "w2", "service": "s", "environment": "production", "features": dict(NORMAL_FEATURES, error_rate=0.4)},
    ]
    r = client.post("/internal/anomaly/score/batch", json={"modelVersion": active["lof"], "schemaVersion": "ops-v1", "items": items}, headers=auth("anomaly.score"))
    assert r.status_code == 200, r.text
    results = r.json()["results"]
    assert [x["windowId"] for x in results] == ["w1", "w2"]
    assert results[1]["score"] > results[0]["score"]


def test_batch_size_is_bounded(client, active):
    items = [{"windowId": f"w{i}", "features": dict(NORMAL_FEATURES)} for i in range(501)]
    r = client.post("/internal/anomaly/score/batch", json={"modelVersion": active["lof"], "schemaVersion": "ops-v1", "items": items}, headers=auth("anomaly.score"))
    assert r.status_code == 422


# ---- service authentication ------------------------------------------------------------------------------------
def test_scoring_requires_a_service_token(client, active):
    assert client.post("/internal/anomaly/score", json=body(active["ocsvm"])).status_code == 401


@pytest.mark.parametrize("kwargs", [{"audience": "other"}, {"issuer": "someone"}, {"key": "x" * 40}, {"expires_in": -120}])
def test_invalid_service_tokens_are_rejected(client, active, kwargs):
    r = client.post("/internal/anomaly/score", json=body(active["ocsvm"]), headers=auth("anomaly.score", **kwargs))
    assert r.status_code == 401


def test_scopes_are_not_interchangeable(client, versions):
    assert client.post(f"/internal/models/{versions['ocsvm']}/activate", headers=auth("anomaly.score")).status_code == 403
    assert client.post("/internal/anomaly/score", json=body(versions["ocsvm"]), headers=auth("models.read")).status_code == 403
    assert client.post("/internal/training/jobs", json={"algorithm": "lof", "source": "benchmark"}, headers=auth("models.admin")).status_code == 403


# ---- registry / activation / health ------------------------------------------------------------------------------
def test_model_listing_and_activation_state(client, versions):
    listing = client.get("/internal/models", headers=auth("models.read")).json()["models"]
    assert {m["modelVersion"] for m in listing} >= set(versions.values())
    assert all("baseline" not in m for m in listing)
    r = client.post(f"/internal/models/{versions['isolation_forest']}/activate", headers=auth("models.admin"))
    assert r.json()["activeInService"] is True
    r = client.post(f"/internal/models/{versions['isolation_forest']}/deactivate", headers=auth("models.admin"))
    assert r.json()["activeInService"] is False
    assert score(client, body(versions["isolation_forest"])).status_code == 409


def test_health_endpoints(client):
    assert client.get("/health/live").json()["status"] == "Healthy"
    ready = client.get("/health/ready")
    assert ready.status_code == 200
    assert ready.json()["checks"]["registry"] == "Healthy"


# ---- retraining --------------------------------------------------------------------------------------------------
def test_feature_window_retraining_registers_inactive_version(client, dataset):
    frame, _ = dataset
    normal = frame[(frame["split"] == "validation") & (frame["label"] == 0)].head(400)
    from app.core.feature_schema import FEATURE_NAMES

    rows = normal[list(FEATURE_NAMES)].astype(float).to_dict(orient="records")
    r = client.post(
        "/internal/training/jobs",
        json={"algorithm": "isolation_forest", "source": "feature-windows", "rows": rows, "requestedBy": "pytest"},
        headers=auth("training.run"),
    )
    assert r.status_code == 202, r.text
    job_id = r.json()["jobId"]
    for _ in range(120):
        job = client.get(f"/internal/training/jobs/{job_id}", headers=auth("training.run")).json()
        if job["status"] in ("succeeded", "failed"):
            break
        time.sleep(0.25)
    assert job["status"] == "succeeded", job
    version = job["modelVersions"][0]
    meta = client.get(f"/internal/models/{version}", headers=auth("models.read")).json()
    assert meta["activeInService"] is False
    assert meta["trainingSource"] == "feature-windows"
    assert score(client, body(version)).status_code == 409


def test_retraining_rejects_secret_columns(client):
    rows = [dict(NORMAL_FEATURES, password=1.0)] * 250
    r = client.post("/internal/training/jobs", json={"algorithm": "lof", "source": "feature-windows", "rows": rows}, headers=auth("training.run"))
    assert r.status_code == 422
