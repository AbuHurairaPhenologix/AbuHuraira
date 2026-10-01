"""Non-causal reason summaries (report §2.8): which features deviate most from the model's training baseline.

This is decision support, not a formal model explanation and never a root-cause claim.
"""

from __future__ import annotations

from app.core.feature_schema import FEATURE_LABELS, FEATURE_NAMES

MIN_ABS_Z = 2.0
MAX_REASONS = 3


def feature_reasons(values: dict[str, float], baseline: dict[str, dict[str, float]]) -> list[dict]:
    reasons = []
    for name in FEATURE_NAMES:
        stats = baseline.get(name, {"mean": 0.0, "std": 1.0})
        std = max(float(stats["std"]), 1e-9)
        z = (float(values[name]) - float(stats["mean"])) / std
        reasons.append(
            {
                "feature": name,
                "value": float(values[name]),
                "baselineMean": float(stats["mean"]),
                "baselineStd": float(stats["std"]),
                "zScore": round(z, 4),
                "direction": "above" if z >= 0 else "below",
            }
        )
    return reasons


def summarize(reasons: list[dict], is_anomaly: bool) -> str:
    notable = sorted((r for r in reasons if abs(r["zScore"]) >= MIN_ABS_Z), key=lambda r: (-abs(r["zScore"]), r["feature"]))[:MAX_REASONS]
    if not notable:
        if is_anomaly:
            return (
                "No single feature deviates strongly from the model's baseline; the score reflects a combination of "
                "moderate deviations. Review the feature vector and related events."
            )
        return "Feature values are within the model's normal baseline."
    parts = [
        f"{'elevated' if r['zScore'] >= 0 else 'reduced'} {FEATURE_LABELS[r['feature']]} "
        f"({r['value']:.4g} vs baseline {r['baselineMean']:.4g})"
        for r in notable
    ]
    text = ", ".join(parts)
    return (
        text[0].upper() + text[1:] + " compared with the model's training baseline. "
        "Indicates where to investigate; not a root-cause determination."
    )
