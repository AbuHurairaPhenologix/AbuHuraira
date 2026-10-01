"""TC-08: secret-like input is excluded or rejected before training; only ops-v1 features enter a model."""

import numpy as np
import pandas as pd
import pytest

from app.core.feature_schema import FEATURE_NAMES
from app.datasets.validation import DatasetValidationError, feature_matrix, rows_to_frame
from tests.conftest import NORMAL_FEATURES


def frame(**extra):
    data = {k: [v] for k, v in NORMAL_FEATURES.items()}
    data.update({k: [v] for k, v in extra.items()})
    return pd.DataFrame(data)


@pytest.mark.parametrize("column", ["password", "Authorization", "access_token", "session_cookie", "client_secret", "api_key", "private_key"])
def test_sensitive_columns_reject_the_dataset(column):
    with pytest.raises(DatasetValidationError, match="sensitive"):
        feature_matrix(frame(**{column: "x"}))


@pytest.mark.parametrize("value", ["Bearer abc.def.ghi", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig", "password=hunter2"])
def test_secret_like_values_reject_the_dataset(value):
    with pytest.raises(DatasetValidationError, match="secret-like"):
        feature_matrix(frame(service=value))


def test_non_feature_columns_are_rejected_and_metadata_excluded():
    with pytest.raises(DatasetValidationError, match="unexpected"):
        feature_matrix(frame(user_email="a@b.c"))
    matrix = feature_matrix(frame(service="orders-api", label=0))
    assert matrix.shape == (1, 8)
    assert list(matrix[0]) == [float(NORMAL_FEATURES[n]) for n in FEATURE_NAMES]


def test_feature_order_is_enforced_even_if_columns_are_shuffled():
    shuffled = frame()[list(reversed(FEATURE_NAMES))]
    assert list(feature_matrix(shuffled)[0]) == [float(NORMAL_FEATURES[n]) for n in FEATURE_NAMES]


def test_invalid_values_are_rejected():
    with pytest.raises(DatasetValidationError, match="missing"):
        feature_matrix(frame().drop(columns=["retry_count"]))
    with pytest.raises(DatasetValidationError, match="non-finite"):
        feature_matrix(frame(avg_duration_ms=np.inf))
    with pytest.raises(DatasetValidationError, match="negative"):
        feature_matrix(frame(avg_duration_ms=-1.0))
    with pytest.raises(DatasetValidationError, match="error_rate"):
        feature_matrix(frame(error_rate=1.5))


def test_retraining_rows_must_have_exactly_the_eight_features():
    with pytest.raises(DatasetValidationError):
        rows_to_frame([{**NORMAL_FEATURES, "authorization_header": 1.0}])
    with pytest.raises(DatasetValidationError):
        rows_to_frame([{k: v for k, v in NORMAL_FEATURES.items() if k != "retry_count"}])
    assert list(rows_to_frame([NORMAL_FEATURES]).columns) == list(FEATURE_NAMES)
