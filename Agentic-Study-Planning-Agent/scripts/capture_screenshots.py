"""Capture README screenshots from the running application.

Prerequisite: the app is running on http://localhost:5220 (dotnet run --project src/StudyPlanner.Web).
Usage:        python scripts/capture_screenshots.py

The script updates one subject's progress through the UI, so the agent really replans before the
schedule and activity pages are captured.
"""
from pathlib import Path

from playwright.sync_api import sync_playwright

BASE = "http://localhost:5220"
OUT = Path(__file__).resolve().parent.parent / "docs" / "screenshots"


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="chrome")
        page = browser.new_page(viewport={"width": 1480, "height": 900}, device_scale_factor=1)

        # Trigger a replan the way a student would: report progress on Linear Algebra.
        page.goto(f"{BASE}/Subjects")
        row = page.locator("tr", has_text="Linear Algebra")
        row.locator("input[name=progress]").fill("70")
        row.locator("button", has_text="Update").click()
        page.wait_for_load_state("networkidle")
        page.screenshot(path=OUT / "subjects.png", full_page=True)

        page.goto(f"{BASE}/")
        page.screenshot(path=OUT / "dashboard.png", full_page=True)

        page.goto(f"{BASE}/Plan")
        page.screenshot(path=OUT / "generate-plan.png", full_page=True)

        page.goto(f"{BASE}/Schedule")
        page.screenshot(path=OUT / "weekly-schedule.png", full_page=True)

        page.goto(f"{BASE}/Activity")
        page.screenshot(path=OUT / "agent-activity.png", full_page=True)

        browser.close()
    print(f"Screenshots written to {OUT}")


if __name__ == "__main__":
    main()
