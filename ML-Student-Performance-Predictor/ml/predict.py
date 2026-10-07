"""Predict final scores with the trained model.

Reads a JSON list of students from stdin and writes a JSON list of predictions to stdout.
This is how the ASP.NET Core application talks to the model.

Input:   [{"attendance": 92, "study_hours": 14, "assignment_avg": 81, "quiz_avg": 77, "previous_exam": 72}, ...]
Output:  [{"predictedScore": 78.4, "category": "Good", "lower": 74.1, "upper": 82.7,
           "contributions": [{"feature": "attendance", "label": "...", "value": 92, "points": 18.1}, ...]}, ...]

Usage:   echo '[{...}]' | python ml/predict.py
"""
import json
import sys

import joblib
import pandas as pd

from common import FEATURE_LABELS, FEATURE_RANGES, FEATURES, METRICS_PATH, MODEL_PATH, categorise


def main() -> None:
    students = json.load(sys.stdin)
    model = joblib.load(MODEL_PATH)
    rmse = json.loads(METRICS_PATH.read_text(encoding="utf-8"))["rmse"]

    frame = pd.DataFrame(students, columns=FEATURES).astype(float)
    for feature, (low, high) in FEATURE_RANGES.items():
        frame[feature] = frame[feature].clip(low, high)

    results = []
    for (_, row), raw in zip(frame.iterrows(), model.predict(frame)):
        score = float(min(max(raw, 0), 100))
        results.append({
            "predictedScore": round(score, 1),
            "category": categorise(score),
            # Typical error band: prediction ± RMSE measured on the test set.
            "lower": round(max(score - rmse, 0), 1),
            "upper": round(min(score + rmse, 100), 1),
            "intercept": round(float(model.intercept_), 2),
            "contributions": [
                {
                    "feature": f,
                    "label": FEATURE_LABELS[f],
                    "value": float(row[f]),
                    "weight": round(float(w), 4),
                    "points": round(float(w * row[f]), 2),
                }
                for f, w in zip(FEATURES, model.coef_)
            ],
        })

    json.dump(results, sys.stdout)


if __name__ == "__main__":
    main()
