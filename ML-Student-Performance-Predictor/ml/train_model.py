"""Train the student performance model and write its evaluation metrics.

Steps
  1. Load the dataset (ml/data/students.csv)
  2. Preprocess: drop incomplete rows, clip features to valid ranges
  3. Describe the data: mean, standard deviation, correlation with the final score
  4. Split 80 / 20 into training and test sets
  5. Fit a Linear Regression model
  6. Evaluate on the unseen test set: MAE, RMSE, R², category accuracy
  7. Save the model (model/model.joblib) and the metrics (model/metrics.json)

Usage:  python ml/train_model.py
"""
import json
from datetime import datetime, timezone

import joblib
import numpy as np
import pandas as pd
from sklearn.linear_model import LinearRegression
from sklearn.metrics import mean_absolute_error, mean_squared_error, r2_score
from sklearn.model_selection import train_test_split

from common import (DATA_PATH, FEATURE_LABELS, FEATURE_RANGES, FEATURES, METRICS_PATH, MODEL_PATH, TARGET,
                    categorise)


def load_and_preprocess() -> pd.DataFrame:
    df = pd.read_csv(DATA_PATH)
    df = df.dropna(subset=FEATURES + [TARGET])
    for feature, (low, high) in FEATURE_RANGES.items():
        df[feature] = df[feature].clip(low, high)
    return df


def describe(df: pd.DataFrame) -> list[dict]:
    """Mean, standard deviation and Pearson correlation with the target for every feature."""
    rows = []
    for column in FEATURES + [TARGET]:
        rows.append({
            "feature": column,
            "label": FEATURE_LABELS.get(column, "Final score"),
            "mean": round(float(df[column].mean()), 2),
            "std": round(float(df[column].std()), 2),
            "min": round(float(df[column].min()), 2),
            "max": round(float(df[column].max()), 2),
            "correlation": round(float(df[column].corr(df[TARGET])), 3),
        })
    return rows


def main() -> None:
    df = load_and_preprocess()
    x, y = df[FEATURES], df[TARGET]
    x_train, x_test, y_train, y_test = train_test_split(x, y, test_size=0.2, random_state=7)

    model = LinearRegression()
    model.fit(x_train, y_train)

    predicted = np.clip(model.predict(x_test), 0, 100)
    errors = y_test.to_numpy() - predicted
    mae = mean_absolute_error(y_test, predicted)
    rmse = float(np.sqrt(mean_squared_error(y_test, predicted)))
    r2 = r2_score(y_test, predicted)

    # Baseline: always predict the training mean. A useful model must beat this.
    baseline = np.full_like(predicted, y_train.mean())
    baseline_mae = mean_absolute_error(y_test, baseline)

    category_hits = sum(categorise(a) == categorise(p) for a, p in zip(y_test, predicted))

    metrics = {
        "trainedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "algorithm": "Linear Regression (ordinary least squares)",
        "rows": len(df),
        "trainRows": len(x_train),
        "testRows": len(x_test),
        "mae": round(float(mae), 2),
        "rmse": round(rmse, 2),
        "r2": round(float(r2), 3),
        "baselineMae": round(float(baseline_mae), 2),
        "categoryAccuracy": round(category_hits / len(y_test), 3),
        "meanError": round(float(errors.mean()), 2),
        "errorStd": round(float(errors.std(ddof=1)), 2),
        "intercept": round(float(model.intercept_), 3),
        "coefficients": [
            {"feature": f, "label": FEATURE_LABELS[f], "weight": round(float(w), 4)}
            for f, w in zip(FEATURES, model.coef_)
        ],
        "statistics": describe(df),
        "correlationMatrix": {
            "features": FEATURES + [TARGET],
            "values": df[FEATURES + [TARGET]].corr().round(3).values.tolist(),
        },
        "testPredictions": [
            {"actual": round(float(a), 1), "predicted": round(float(p), 1)}
            for a, p in zip(y_test, predicted)
        ],
    }

    MODEL_PATH.parent.mkdir(parents=True, exist_ok=True)
    joblib.dump(model, MODEL_PATH)
    METRICS_PATH.write_text(json.dumps(metrics, indent=2), encoding="utf-8")

    print(f"Trained on {len(x_train)} rows, evaluated on {len(x_test)} rows")
    print(f"MAE  = {mae:.2f} points   (baseline that always predicts the mean: {baseline_mae:.2f})")
    print(f"RMSE = {rmse:.2f} points")
    print(f"R^2  = {r2:.3f}")
    print(f"Category accuracy = {metrics['categoryAccuracy']:.1%}")
    print("Final score = {:.2f} {}".format(
        model.intercept_, " ".join(f"{w:+.3f}*{f}" for f, w in zip(FEATURES, model.coef_))))


if __name__ == "__main__":
    main()
