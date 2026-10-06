"""Score an analyzed demo recording against sample-data/demo_ground_truth.json.

The demo is synthetic, so the exact timing of every utterance, event and the planted anomaly is known.
This script compares the API's output with that ground truth and writes docs/results/demo-evaluation.{json,md}.

Usage (backend running, demo uploaded and analyzed):
    python scripts/evaluate_demo.py [--api http://localhost:8000] [--id 1]
"""
import argparse
import itertools
import json
import re
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EVENT_CATEGORY = {"Music": "Music", "Applause": "Applause", "Keyboard": "Keyboard", "Door": "Door", "Siren": "Siren",
                  "Alarm": "Alarm", "Anomaly (glass shatter)": "Impact"}


def get(api: str, path: str):
    with urllib.request.urlopen(f"{api}{path}") as r:
        return json.loads(r.read())


def words(text: str) -> list[str]:
    return re.findall(r"[a-z0-9']+", text.lower().replace("10", "ten"))


def wer(ref: list[str], hyp: list[str]) -> float:
    d = list(range(len(hyp) + 1))
    for i, r in enumerate(ref, 1):
        prev, d[0] = d[0], i
        for j, h in enumerate(hyp, 1):
            prev, d[j] = d[j], min(d[j] + 1, d[j - 1] + 1, prev + (r != h))
    return d[-1] / max(len(ref), 1)


def overlap(a0, a1, b0, b1):
    return max(0.0, min(a1, b1) - max(a0, b0))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--api", default="http://localhost:8000")
    ap.add_argument("--id", type=int, default=1)
    args = ap.parse_args()
    base = f"/api/audio/{args.id}"
    gt = json.loads((ROOT / "sample-data" / "demo_ground_truth.json").read_text())
    detail = get(args.api, base)
    transcript = get(args.api, f"{base}/transcript")
    events = get(args.api, f"{base}/events")
    anomalies = get(args.api, f"{base}/anomalies")["anomalies"]

    speech = [e for e in gt["elements"] if e["type"] == "speech"]
    # 1. Word error rate over the whole transcript
    ref = [w for e in speech for w in words(e["text"])]
    hyp = [w for s in transcript for w in words(s["text"])]
    w = wer(ref, hyp)

    # 2. Speaker attribution: map predicted clusters to true speakers with the best permutation (by time overlap)
    pred_names = sorted({s["speaker"] for s in transcript if s["speaker"]})
    true_names = sorted({e["speaker"] for e in speech})

    def truth_at(s):
        best = max(speech, key=lambda e: overlap(s["start"], s["end"], e["start"], e["end"]))
        return best["speaker"] if overlap(s["start"], s["end"], best["start"], best["end"]) > 0 else None

    labelled = [(s, truth_at(s)) for s in transcript if s["speaker"]]
    best_acc, best_map = 0.0, {}
    for perm in itertools.permutations(true_names, min(len(true_names), len(pred_names))):
        mapping = dict(zip(pred_names, perm))
        dur = sum(s["end"] - s["start"] for s, _ in labelled)
        ok = sum(s["end"] - s["start"] for s, t in labelled if mapping.get(s["speaker"]) == t)
        if dur and ok / dur > best_acc:
            best_acc, best_map = ok / dur, mapping

    # 3. Transcript timing: start offset of the first segment overlapping each true utterance
    offsets = []
    for e in speech:
        segs = [s for s in transcript if overlap(s["start"], s["end"], e["start"], e["end"]) > 0]
        if segs:
            offsets.append(abs(min(s["start"] for s in segs) - e["start"]))

    # 4. Sound events: was each planted event detected (same category, overlapping in time)?
    planted = []
    for e in gt["elements"]:
        if e["type"] == "speech":
            continue
        cat = EVENT_CATEGORY[e["label"]]
        hit = next((d for d in events if d["label"] == cat and overlap(d["start"], d["end"], e["start"] - 1, e["end"] + 1) > 0), None)
        planted.append({"label": e["label"], "start": e["start"], "end": e["end"], "category": cat,
                        "detected": hit is not None, "confidence": hit["confidence"] if hit else None})
    non_speech = [d for d in events if d["label"] not in ("Speech", "Silence")]
    unmatched = [d for d in non_speech if not any(
        overlap(d["start"], d["end"], e["start"] - 1, e["end"] + 1) > 0 for e in gt["elements"] if e["type"] != "speech")]

    # 5. Anomaly: is the planted glass shatter flagged, and how many other regions were flagged?
    truth_anom = next(e for e in gt["elements"] if e["type"] == "anomaly")
    anomaly_hits = [a for a in anomalies if overlap(a["start"], a["end"], truth_anom["start"] - 0.5, truth_anom["end"] + 0.5) > 0]

    result = {
        "recording": detail["filename"], "duration_s": detail["duration_s"], "analysis_time_s": (detail["stats"] or {}).get("total_s"),
        "transcription": {"reference_words": len(ref), "hypothesis_words": len(hyp), "wer": round(w, 4),
                          "mean_start_offset_s": round(sum(offsets) / len(offsets), 2) if offsets else None},
        "speakers": {"true": len(true_names), "predicted": len(pred_names), "attribution_accuracy": round(best_acc, 4), "mapping": best_map},
        "events": {"planted": planted, "recall": round(sum(p["detected"] for p in planted) / len(planted), 3),
                   "non_speech_detections": len(non_speech), "detections_without_planted_source": len(unmatched)},
        "anomaly": {"planted_at": truth_anom["start"], "detected": bool(anomaly_hits),
                    "score": anomaly_hits[0]["score"] if anomaly_hits else None,
                    "other_regions_flagged": len(anomalies) - len(anomaly_hits)},
    }
    out = ROOT / "docs" / "results"
    out.mkdir(parents=True, exist_ok=True)
    (out / "demo-evaluation.json").write_text(json.dumps(result, indent=2))

    rows = "\n".join(f"| {p['label']} | {p['start']:.1f}–{p['end']:.1f} s | {p['category']} | "
                     f"{'✅ ' + format(p['confidence'], '.2f') if p['detected'] else '❌ missed'} |" for p in planted)
    md = f"""# Demo evaluation — `{result['recording']}`

Generated by `scripts/evaluate_demo.py` from the live API output and `sample-data/demo_ground_truth.json`.

| Metric | Result |
|---|---|
| Word error rate (Whisper-base) | **{w * 100:.1f}%** ({len(ref)} reference words) |
| Mean transcript start offset | **{result['transcription']['mean_start_offset_s']} s** |
| Speakers (true / found) | **{len(true_names)} / {len(pred_names)}** |
| Speaker attribution accuracy (by duration) | **{best_acc * 100:.1f}%** |
| Planted sound events detected | **{sum(p['detected'] for p in planted)} / {len(planted)}** |
| Non-speech detections without a planted source | **{len(unmatched)}** |
| Planted anomaly flagged | **{'yes' if anomaly_hits else 'no'}**{f" (score {anomaly_hits[0]['score']:.3f})" if anomaly_hits else ''} |
| Other regions flagged as anomalous | **{result['anomaly']['other_regions_flagged']}** |
| Analysis time (CPU) | **{result['analysis_time_s']} s** for {detail['duration_s']:.0f} s of audio |

## Planted events

| Planted element | Time | Expected category | Detected |
|---|---|---|---|
{rows}
"""
    (out / "demo-evaluation.md").write_text(md, encoding="utf-8")
    print(md.encode("ascii", "replace").decode() if not __import__("sys").stdout.encoding.lower().startswith("utf") else md)


if __name__ == "__main__":
    main()
