"""Capture README screenshots from the running EchoSense AI application.

Prerequisites: API on http://localhost:8000, Vite dev server on http://localhost:5173, the demo recording uploaded
and analyzed (id 1). Optionally a second uploaded recording (--processing-id) is re-analyzed so the
upload/processing view can be captured mid-pipeline.

Usage:  python scripts/capture_screenshots.py [--id 1] [--processing-id 2] [--out docs/screenshots]
        [--browser-channel msedge]
Every image is a real capture of the running application with its computed analysis results.
"""
import argparse
import json
import time
import urllib.request
from pathlib import Path

from playwright.sync_api import Page, sync_playwright

API = "http://localhost:8000"
WEB = "http://localhost:5173"


def open_page(page: Page, route: str, audio_id: int, settle_ms: int = 2500) -> None:
    page.goto(f"{WEB}/#/{route}/{audio_id}")
    page.reload()
    page.wait_for_selector(".main .card")
    page.wait_for_timeout(settle_ms)


def card(page: Page, title: str):
    return page.locator(".card", has=page.locator(".card-title", has_text=title)).first


def play_from_transcript(page: Page, text: str, seconds: float = 1.2) -> None:
    """Click a transcript line (seeks + plays), let it play briefly, then pause - shows synced highlight & playhead."""
    page.locator(".tseg", has_text=text).first.click()
    page.wait_for_timeout(int(seconds * 1000))
    page.locator(".icon-btn").first.click()
    page.wait_for_timeout(400)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--id", type=int, default=1)
    ap.add_argument("--processing-id", type=int, default=None)
    ap.add_argument("--out", default="docs/screenshots")
    ap.add_argument("--browser-channel", default="msedge", help="installed browser to drive (msedge, chrome) or '' for bundled chromium")
    args = ap.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    with sync_playwright() as p:
        browser = p.chromium.launch(channel=args.browser_channel or None,
                                    args=["--autoplay-policy=no-user-gesture-required", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"])
        page = browser.new_page(viewport={"width": 1600, "height": 1000}, device_scale_factor=1.5)
        shots: list[str] = []

        def shot(name: str, target=None, full_page: bool = False) -> None:
            path = out / f"{name}.png"
            (target.screenshot(path=str(path)) if target is not None else page.screenshot(path=str(path), full_page=full_page))
            shots.append(path.name)

        # Dashboard: play from the moment right after the planted anomaly
        open_page(page, "dashboard", args.id)
        play_from_transcript(page, "Wow, that was loud")
        shot("dashboard-overview", full_page=True)
        shot("dashboard-hero")
        shot("ai-summary", card(page, "AI recording summary"))
        shot("predictive-analytics", card(page, "Predictive analytics"))
        shot("speaker-analysis", card(page, "Speaker analysis"))
        shot("waveform-player", page.locator(".card").filter(has=page.locator(".player")).first)

        # Synchronized timeline analysis
        open_page(page, "analysis", args.id)
        play_from_transcript(page, "Sound event detection separates")
        shot("timeline-analysis", full_page=True)
        shot("synchronized-timeline", card(page, "Synchronized analysis timeline"))
        shot("anomaly-detection", card(page, "Detected anomalies"))
        shot("sound-events", card(page, "Sound events"))

        # Semantic search
        open_page(page, "search", args.id)
        page.fill(".search-box input", "Where did they discuss artificial intelligence?")
        page.click(".search-box button")
        page.wait_for_selector(".hit")
        page.wait_for_timeout(600)
        shot("semantic-search", full_page=True)
        page.locator(".suggestions .chip", has_text="Where did the audience applaud?").click()
        page.wait_for_timeout(1500)
        shot("semantic-search-events", card(page, "Semantic audio search"))

        # Spatial audio world: trigger Speaker 2 so the scene shows a playing source
        open_page(page, "spatial", args.id, settle_ms=3000)
        page.locator(".tseg", has_text="Speaker 2").first.click()
        page.wait_for_timeout(1800)
        shot("spatial-audio-world", full_page=True)
        shot("spatial-scene", page.locator(".spatial-wrap"))

        # Upload & processing view
        if args.processing_id:
            urllib.request.urlopen(urllib.request.Request(f"{API}/api/audio/{args.processing_id}/analyze", method="POST"))
            open_page(page, "library", args.processing_id, settle_ms=500)
            deadline = time.time() + 120
            while time.time() < deadline:
                with urllib.request.urlopen(f"{API}/api/audio/{args.processing_id}") as r:
                    stage = json.loads(r.read())["stage"]
                if stage in ("Speech transcription", "Speaker analysis", "Emotion & prosody"):
                    break
                time.sleep(0.5)
            page.wait_for_timeout(2200)
            shot("upload-processing", full_page=True)
        else:
            open_page(page, "library", args.id, settle_ms=800)
            shot("upload-library", full_page=True)

        browser.close()
    print("captured:", ", ".join(shots))


if __name__ == "__main__":
    main()
