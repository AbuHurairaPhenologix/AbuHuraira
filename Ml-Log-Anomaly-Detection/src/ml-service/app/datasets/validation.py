"""Training-data validation (report §3.3, TC-08).

A training dataset is an extension of the logging system, not an unrestricted copy of events. Only the eight
``ops-v1`` operational quantities may enter a model. Any column that looks like credential material (password,
token, authorization header, cookie, secret, API key, private key...) causes the dataset to be **rejected** before
training, and any other non-feature column is **excluded** from the model matrix.
"""

from __future__ import annotations

import re

import numpy as np
import pandas as pd

from app.core.feature_schema import FEATURE_NAMES, RATIO_FEATURES

# Same sensitive-key vocabulary as the .NET EventSanitizer.
SENSITIVE_COLUMN = re.compile(
    r"pass(word|wd|phrase)?|pwd|secret|token|authori[sz]ation|auth[_\-]?header|cookie|session|api[_\-]?key|apikey|"
    r"client[_\-]?secret|credential|private[_\-]?key|bearer|jwt|signature|credit[_\-]?card|card[_\-]?number|cvv",
    re.IGNORECASE,
)

SECRET_VALUE = re.compile(
    r"eyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.|\bbearer\s+\S+|-----BEGIN [A-Z ]*PRIVATE KEY-----|"
    r"\b(password|secret|token|api[_\-]?key)\s*[=:]\s*\S+",
    re.IGNORECASE,
)

# Metadata columns allowed alongside features in benchmark files (never used as model inputs).
METADATA_COLUMNS = frozenset(
    {"window_id", "service", "environment", "window_start_utc", "window_end_utc", "split", "label", "anomaly_type"}
)


class DatasetValidationError(ValueError):
    """Raised when a dataset must not be used for training."""


def assert_no_sensitive_columns(frame: pd.DataFrame) -> None:
    sensitive = [c for c in frame.columns if SENSITIVE_COLUMN.search(str(c))]
    if sensitive:
        raise DatasetValidationError(f"Dataset rejected: sensitive columns present {sorted(sensitive)}")
    for column in frame.columns:
        if pd.api.types.is_object_dtype(frame[column]) or pd.api.types.is_string_dtype(frame[column]):
            values = frame[column].dropna().astype(str)
            if values.map(lambda v: bool(SECRET_VALUE.search(v))).any():
                raise DatasetValidationError(f"Dataset rejected: secret-like values in column '{column}'")


def feature_matrix(frame: pd.DataFrame) -> np.ndarray:
    """Validate a frame and return the model matrix in exact ``ops-v1`` order.

    * rejects sensitive columns / secret-like values (TC-08),
    * rejects missing features,
    * excludes every non-feature column,
    * rejects non-numeric, non-finite, negative or out-of-range values.
    """
    assert_no_sensitive_columns(frame)
    missing = [f for f in FEATURE_NAMES if f not in frame.columns]
    if missing:
        raise DatasetValidationError(f"Dataset rejected: missing features {missing}")

    unexpected = [c for c in frame.columns if c not in FEATURE_NAMES and c not in METADATA_COLUMNS]
    if unexpected:
        raise DatasetValidationError(f"Dataset rejected: unexpected columns {sorted(unexpected)}")

    matrix = frame.loc[:, list(FEATURE_NAMES)]
    try:
        values = matrix.to_numpy(dtype=float)
    except (TypeError, ValueError) as exc:
        raise DatasetValidationError("Dataset rejected: non-numeric feature values") from exc
    if not np.isfinite(values).all():
        raise DatasetValidationError("Dataset rejected: non-finite feature values")
    if (values < 0).any():
        raise DatasetValidationError("Dataset rejected: negative feature values")
    for name in RATIO_FEATURES:
        idx = FEATURE_NAMES.index(name)
        if (values[:, idx] > 1.0).any():
            raise DatasetValidationError(f"Dataset rejected: {name} must be within [0, 1]")
    return values


def rows_to_frame(rows: list[dict[str, float]]) -> pd.DataFrame:
    """Build a frame from feature dictionaries (used for feature-window retraining)."""
    for row in rows:
        keys = set(row)
        if keys != set(FEATURE_NAMES):
            extra = sorted(keys - set(FEATURE_NAMES))
            missing = sorted(set(FEATURE_NAMES) - keys)
            raise DatasetValidationError(f"Row rejected: missing={missing} unexpected={extra}")
    return pd.DataFrame(rows, columns=list(FEATURE_NAMES))
