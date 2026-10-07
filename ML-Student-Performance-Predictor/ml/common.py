"""Shared definitions for training and prediction."""
from pathlib import Path

ML_DIR = Path(__file__).parent
DATA_PATH = ML_DIR / "data" / "students.csv"
MODEL_PATH = ML_DIR / "model" / "model.joblib"
METRICS_PATH = ML_DIR / "model" / "metrics.json"

FEATURES = ["attendance", "study_hours", "assignment_avg", "quiz_avg", "previous_exam"]
TARGET = "final_score"

FEATURE_LABELS = {
    "attendance": "Attendance (%)",
    "study_hours": "Study hours / week",
    "assignment_avg": "Assignment average",
    "quiz_avg": "Quiz average",
    "previous_exam": "Previous exam score",
}

# Valid input ranges, used for preprocessing (clipping) and form validation.
FEATURE_RANGES = {
    "attendance": (0.0, 100.0),
    "study_hours": (0.0, 40.0),
    "assignment_avg": (0.0, 100.0),
    "quiz_avg": (0.0, 100.0),
    "previous_exam": (0.0, 100.0),
}


def categorise(score: float) -> str:
    """Map a predicted final score to a performance band."""
    if score >= 85:
        return "Excellent"
    if score >= 70:
        return "Good"
    if score >= 55:
        return "Average"
    return "At Risk"
