"""Capture README screenshots from the running application.

Prerequisite: the app is running on http://localhost:5210 (dotnet run --project src/StudentPredictor.Web).
Usage:        python scripts/capture_screenshots.py

The script submits one real prediction through the form, so the result page shows live model output.
"""
from pathlib import Path

from playwright.sync_api import sync_playwright

BASE = "http://localhost:5210"
OUT = Path(__file__).resolve().parent.parent / "docs" / "screenshots"


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="chrome")
        page = browser.new_page(viewport={"width": 1480, "height": 900}, device_scale_factor=1)

        page.goto(f"{BASE}/")
        page.screenshot(path=OUT / "dashboard.png", full_page=True)

        page.goto(f"{BASE}/Predict")
        page.fill("#Input_StudentName", "Maryam Rahman")
        page.click("text=Struggling student")
        page.fill("#Input_StudyHours", "6")
        page.dispatch_event("#Input_StudyHours", "input")
        page.screenshot(path=OUT / "prediction-form.png", full_page=True)

        page.click("button[type=submit]")
        page.wait_for_url("**/Result/**")
        page.screenshot(path=OUT / "prediction-result.png", full_page=True)

        page.goto(f"{BASE}/Students")
        page.screenshot(path=OUT / "student-records.png", full_page=True)

        page.goto(f"{BASE}/Model")
        page.screenshot(path=OUT / "model-statistics.png", full_page=True)

        browser.close()
    print(f"Screenshots written to {OUT}")


if __name__ == "__main__":
    main()
