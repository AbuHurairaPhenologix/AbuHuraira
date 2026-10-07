"""Study planning agent.

Reads the student's subjects and weekly availability as JSON from stdin, runs a fixed decision
cycle and writes the plan, plus a log of every decision, as JSON to stdout.

Decision cycle
  1. Analyze Subjects      remaining work, days to the exam, hours needed this week
  2. Calculate Priority    PriorityScore = PriorityWeight × DifficultyWeight × UrgencyWeight
  3. Check Available Time  hours per day in the planning window, workload ratio
  4. Detect Conflicts      deadline overload, exam clashes, low progress near an exam, overload
  5. Allocate Study Hours  reserve hours for exams inside the window, share the rest by score
  6. Generate Weekly Plan  place 30-minute blocks day by day (least slack first), merge them into sessions
  7. Replan Schedule       compare with the previous plan and explain what changed (when one exists)

Only the Python standard library is used.

Usage:  python planner/agent.py < input.json
"""
import json
import math
import sys
from datetime import date, datetime, timedelta

BLOCK = 0.5                     # hours: smallest unit of study time the agent schedules
MAX_PER_SUBJECT_PER_DAY = 3.0   # hours: avoid cramming one subject for a whole evening
BREAK_MINUTES = 10              # pause inserted between two sessions on the same day
HORIZON_DAYS = 7

PRIORITY_WEIGHTS = {"Low": 1.0, "Medium": 1.5, "High": 2.0, "Critical": 2.5}
DAY_NAMES = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]


# ---------------------------------------------------------------- weights

def difficulty_weight(difficulty: int) -> float:
    """1 (easy) → 0.8 ... 5 (very hard) → 1.6"""
    return 0.6 + 0.2 * difficulty


def urgency_weight(days_left: int) -> float:
    """Exponential decay: 4.0 on exam day, ~2.1 one week before, ~1.4 two weeks before, → 1.0 far away."""
    return 1 + 3 * math.exp(-max(days_left, 0) / 7)


def round_block(hours: float) -> float:
    return math.floor(hours / BLOCK + 1e-9) * BLOCK


# ---------------------------------------------------------------- agent

class StudyPlanningAgent:
    def __init__(self, request: dict):
        self.today = date.fromisoformat(request["today"])
        self.days = [self.today + timedelta(days=i) for i in range(HORIZON_DAYS)]
        availability = {a["dayOfWeek"]: a for a in request["availability"]}
        self.capacity = [float(availability.get(d.weekday(), {}).get("hours", 0)) for d in self.days]
        self.start_times = [availability.get(d.weekday(), {}).get("startTime", "18:00") for d in self.days]
        self.subjects = [dict(s) for s in request["subjects"]]
        self.previous = request.get("previous")
        self.trigger = request.get("trigger") or "Manual run"
        self.steps: list[dict] = []
        self.conflicts: list[dict] = []
        self.sessions: list[dict] = []

    def log(self, action: str, summary: str, details: list[str], status: str = "ok") -> None:
        self.steps.append({"action": action, "status": status, "summary": summary, "details": details})

    def run(self) -> dict:
        self.analyze_subjects()
        self.calculate_priority()
        self.check_available_time()
        self.detect_conflicts()
        self.allocate_hours()
        self.generate_plan()
        changes = self.replan()
        return self.result(changes)

    # 1 ---------------------------------------------------------------
    def analyze_subjects(self) -> None:
        details, active = [], []
        for s in self.subjects:
            exam = date.fromisoformat(s["examDate"])
            s["daysLeft"] = (exam - self.today).days
            s["remainingHours"] = round(s["totalHours"] * (100 - s["progress"]) / 100, 1)
            if s["daysLeft"] <= 0:
                s["status"] = "Exam passed" if s["daysLeft"] < 0 else "Exam today"
                s["weeklyDemand"] = 0.0
                details.append(f"{s['name']}: exam is {'today' if s['daysLeft'] == 0 else 'over'} - excluded from planning.")
                continue
            if s["remainingHours"] <= 0:
                s["status"] = "Completed"
                s["weeklyDemand"] = 0.0
                details.append(f"{s['name']}: 100% complete - nothing left to schedule.")
                continue
            s["status"] = "Active"
            # Pace needed to finish on time: spread the remaining work evenly until the exam.
            s["weeklyDemand"] = round(s["remainingHours"] * min(1.0, HORIZON_DAYS / s["daysLeft"]), 1)
            active.append(s)
            details.append(
                f"{s['name']}: {s['progress']}% done, {s['remainingHours']} h of work left, exam in {s['daysLeft']} days "
                f"→ needs {s['weeklyDemand']} h this week.")
        self.active = active
        demand = sum(s["weeklyDemand"] for s in active)
        self.weekly_demand = round(demand, 1)
        self.log("Analyze Subjects",
                 f"{len(active)} of {len(self.subjects)} subjects need study time; {self.weekly_demand} h needed this week.",
                 details)

    # 2 ---------------------------------------------------------------
    def calculate_priority(self) -> None:
        details = []
        for s in self.subjects:
            s["priorityWeight"] = PRIORITY_WEIGHTS.get(s["priority"], 1.0)
            s["difficultyWeight"] = round(difficulty_weight(s["difficulty"]), 2)
            s["urgencyWeight"] = round(urgency_weight(s["daysLeft"]), 2)
            s["score"] = round(s["priorityWeight"] * s["difficultyWeight"] * s["urgencyWeight"], 2) \
                if s["status"] == "Active" else 0.0
        ranked = sorted(self.active, key=lambda s: -s["score"])
        for rank, s in enumerate(ranked, start=1):
            details.append(f"#{rank} {s['name']}: {s['priorityWeight']} × {s['difficultyWeight']} × {s['urgencyWeight']} = {s['score']}")
        self.top = ranked[0] if ranked else None
        summary = f"Highest priority: {self.top['name']} (score {self.top['score']})." if self.top else "No active subjects."
        self.log("Calculate Priority", summary, details)

    # 3 ---------------------------------------------------------------
    def check_available_time(self) -> None:
        self.available = round(sum(self.capacity), 1)
        self.workload_ratio = round(self.weekly_demand / self.available, 2) if self.available else 0.0
        details = [f"{d.strftime('%a %d %b')}: {c:g} h from {t}" for d, c, t in zip(self.days, self.capacity, self.start_times)]
        status = "warning" if self.workload_ratio > 1 else "ok"
        self.log("Check Available Time",
                 f"{self.available} h available in the next {HORIZON_DAYS} days; workload ratio {self.workload_ratio:.2f} "
                 f"({self.weekly_demand} h needed / {self.available} h available).",
                 details, status)

    # 4 ---------------------------------------------------------------
    def hours_before(self, exam_days_left: int) -> float:
        """Capacity on the days strictly before the exam (the exam day itself is not used for study)."""
        return sum(self.capacity[:min(exam_days_left, HORIZON_DAYS)])

    def conflict(self, kind: str, severity: str, message: str, resolution: str) -> None:
        self.conflicts.append({"type": kind, "severity": severity, "message": message, "resolution": resolution})

    def detect_conflicts(self) -> None:
        in_window = sorted((s for s in self.active if s["daysLeft"] <= HORIZON_DAYS), key=lambda s: s["daysLeft"])

        # a) cumulative deadline check: can all exams up to date X be prepared with the hours before X?
        needed = 0.0
        for s in in_window:
            needed += s["remainingHours"]
            capacity = self.hours_before(s["daysLeft"])
            if needed > capacity:
                self.conflict("Deadline overload", "high",
                              f"Exams up to {s['name']} need {needed:g} h but only {capacity:g} h are free before {s['examDate']}.",
                              "Reserve every free hour before the exam for the subjects due first.")

        # b) two exams within one day of each other
        dated = sorted(self.active, key=lambda s: s["daysLeft"])
        for a, b in zip(dated, dated[1:]):
            if b["daysLeft"] - a["daysLeft"] <= 1:
                self.conflict("Exam clash", "medium",
                              f"{a['name']} and {b['name']} exams are {b['daysLeft'] - a['daysLeft']} day(s) apart.",
                              "Both get daily sessions before their exams (least-slack-first placement), "
                              "so neither is left to the last evening.")

        # c) low progress close to the exam
        for s in self.active:
            if s["daysLeft"] <= 4 and s["progress"] < 50:
                self.conflict("Low progress", "high",
                              f"{s['name']} is only {s['progress']}% complete with {s['daysLeft']} day(s) left.",
                              "Raised urgency weight; daily sessions until the exam.")

        # d) more work than time
        if self.workload_ratio > 1:
            self.conflict("Overload", "medium",
                          f"{self.weekly_demand} h of study needed but {self.available} h available "
                          f"({(self.workload_ratio - 1) * 100:.0f}% over capacity).",
                          "Allocate by priority score; lower-scored subjects get a reduced share this week.")

        # e) days without study time before an exam
        for i, (d, c) in enumerate(zip(self.days, self.capacity)):
            if c == 0 and any(i < s["daysLeft"] <= HORIZON_DAYS for s in self.active):
                self.conflict("No study time", "low", f"No study time on {d.strftime('%A')} although an exam follows.",
                              "Hours moved to the surrounding days.")

        if self.conflicts:
            self.log("Detect Conflicts", f"{len(self.conflicts)} conflict(s) found and handled.",
                     [f"[{c['severity']}] {c['type']}: {c['message']} → {c['resolution']}" for c in self.conflicts], "warning")
        else:
            self.log("Detect Conflicts", "No scheduling conflicts found.", ["All exams can be prepared within the available time."])

    # 5 ---------------------------------------------------------------
    def allocate_hours(self) -> None:
        details = []
        for s in self.subjects:
            s["allocatedHours"] = 0.0
            # A subject can never use more than its pace for the week or the hours that exist before its exam.
            s["cap"] = min(s["weeklyDemand"], self.hours_before(s["daysLeft"])) if s["status"] == "Active" else 0.0

        # Phase A: reserve hours for exams inside the window, earliest exam first.
        reserved_total = 0.0
        for s in sorted((s for s in self.active if s["daysLeft"] <= HORIZON_DAYS), key=lambda s: s["daysLeft"]):
            free_before = self.hours_before(s["daysLeft"]) - reserved_total
            reserve = round_block(max(0.0, min(s["cap"], free_before)))
            s["allocatedHours"] = reserve
            reserved_total += reserve
            if reserve:
                details.append(f"Reserved {reserve:g} h for {s['name']} (exam in {s['daysLeft']} days).")

        # Phase B: share the remaining hours by priority score (water-filling: a subject that reaches its cap
        # hands its unused share back to the others).
        pool = round_block(self.available - reserved_total)
        open_subjects = [s for s in self.active if s["allocatedHours"] < s["cap"]]
        while pool >= BLOCK and open_subjects:
            total_score = sum(s["score"] for s in open_subjects)
            given = 0.0
            for s in open_subjects:
                share = round_block(pool * s["score"] / total_score)
                share = min(share, round_block(s["cap"] - s["allocatedHours"]))
                s["allocatedHours"] += share
                given += share
            if given == 0:  # shares smaller than one block: hand single blocks to the highest scores
                for s in sorted(open_subjects, key=lambda s: -s["score"]):
                    if pool - given >= BLOCK and s["cap"] - s["allocatedHours"] >= BLOCK:
                        s["allocatedHours"] += BLOCK
                        given += BLOCK
                if given == 0:
                    break
            pool = round_block(pool - given)
            open_subjects = [s for s in open_subjects if s["cap"] - s["allocatedHours"] >= BLOCK]

        for s in sorted(self.active, key=lambda s: -s["score"]):
            s["coverage"] = round(s["allocatedHours"] / s["weeklyDemand"], 2) if s["weeklyDemand"] else 1.0
            details.append(f"{s['name']}: {s['allocatedHours']:g} h of {s['weeklyDemand']:g} h needed ({s['coverage'] * 100:.0f}%).")
        self.allocated = sum(s["allocatedHours"] for s in self.subjects)
        self.log("Allocate Study Hours",
                 f"{self.allocated:g} h allocated: {reserved_total:g} h reserved for upcoming exams, "
                 f"{self.allocated - reserved_total:g} h shared by priority score.",
                 details)

    # 6 ---------------------------------------------------------------
    def slack(self, subject: dict, day_index: int, hours_left: float) -> float:
        last_day = min(subject["daysLeft"], HORIZON_DAYS)
        later = sum(min(c, MAX_PER_SUBJECT_PER_DAY) for c in self.capacity[day_index + 1:last_day])
        return later - hours_left

    def generate_plan(self) -> None:
        remaining = {s["id"]: s["allocatedHours"] for s in self.active}
        by_id = {s["id"]: s for s in self.active}
        unplaced_details = []

        for i, day in enumerate(self.days):
            free = self.capacity[i]
            used: dict[int, float] = {}
            order: list[int] = []
            while free >= BLOCK:
                # Eligible: exam after today, hours still to place, daily limit not reached.
                candidates = [s for s in self.active
                              if s["daysLeft"] > i and remaining[s["id"]] >= BLOCK
                              and used.get(s["id"], 0) < MAX_PER_SUBJECT_PER_DAY]
                if not candidates:
                    break
                # Least slack first: study the subject that can least afford to wait. Slack is the time it could
                # still get on later days before its exam minus the hours it still needs.
                best = min(candidates, key=lambda s: (self.slack(s, i, remaining[s["id"]]), -s["score"]))
                used[best["id"]] = used.get(best["id"], 0) + BLOCK
                remaining[best["id"]] -= BLOCK
                free -= BLOCK
                if best["id"] not in order:
                    order.append(best["id"])

            # Turn the blocks into timed sessions, most important subject first while concentration is highest.
            clock = datetime.combine(day, datetime.strptime(self.start_times[i], "%H:%M").time())
            for sid in sorted(order, key=lambda x: -by_id[x]["score"]):
                hours = used[sid]
                end = clock + timedelta(hours=hours)
                self.sessions.append({
                    "subjectId": sid, "subject": by_id[sid]["name"], "date": day.isoformat(),
                    "start": clock.strftime("%H:%M"), "end": end.strftime("%H:%M"), "hours": hours,
                })
                clock = end + timedelta(minutes=BREAK_MINUTES)

        for sid, left in remaining.items():
            if left >= BLOCK:
                unplaced_details.append(f"{by_id[sid]['name']}: {left:g} h could not be placed before the exam.")
                self.conflict("Unplaced hours", "medium", unplaced_details[-1], "Increase availability before the exam.")

        days_used = len({s["date"] for s in self.sessions})
        details = [f"{d.strftime('%a %d %b')}: " + (", ".join(
            f"{s['subject']} {s['start']}–{s['end']}" for s in self.sessions if s["date"] == d.isoformat()) or "rest day")
            for d in self.days] + unplaced_details
        self.log("Generate Weekly Plan",
                 f"{len(self.sessions)} study sessions over {days_used} days "
                 f"(least slack first, max {MAX_PER_SUBJECT_PER_DAY:g} h per subject per day, {BREAK_MINUTES}-minute breaks).",
                 details, "warning" if unplaced_details else "ok")

    # 7 ---------------------------------------------------------------
    def replan(self) -> list[dict]:
        if not self.previous:
            return []
        before = {a["subjectId"]: a["hours"] for a in self.previous.get("allocations", [])}
        old_subjects = {s["id"]: s for s in self.previous.get("subjects", [])}
        changes, details = [], []
        for s in self.subjects:
            old_hours = before.get(s["id"], 0.0)
            delta = round(s["allocatedHours"] - old_hours, 1)
            reasons = []
            old = old_subjects.get(s["id"])
            if old is None:
                reasons.append("new subject")
            else:
                if old["progress"] != s["progress"]:
                    reasons.append(f"progress {old['progress']}% → {s['progress']}%")
                if old["examDate"] != s["examDate"]:
                    reasons.append(f"exam moved {old['examDate']} → {s['examDate']}")
                if old["priority"] != s["priority"]:
                    reasons.append(f"priority {old['priority']} → {s['priority']}")
                if old["difficulty"] != s["difficulty"]:
                    reasons.append(f"difficulty {old['difficulty']} → {s['difficulty']}")
            if delta != 0 or reasons:
                changes.append({"subjectId": s["id"], "subject": s["name"], "before": old_hours,
                                "after": s["allocatedHours"], "delta": delta, "reason": ", ".join(reasons) or "rebalanced"})
                details.append(f"{s['name']}: {old_hours:g} h → {s['allocatedHours']:g} h ({delta:+g} h) - "
                               f"{', '.join(reasons) or 'share rebalanced after other changes'}.")
        for sid, old in old_subjects.items():
            if all(s["id"] != sid for s in self.subjects):
                changes.append({"subjectId": sid, "subject": old["name"], "before": before.get(sid, 0.0), "after": 0.0,
                                "delta": -before.get(sid, 0.0), "reason": "subject removed"})
                details.append(f"{old['name']}: removed, {before.get(sid, 0.0):g} h freed for other subjects.")

        self.log("Replan Schedule",
                 f"Triggered by: {self.trigger}. {len(changes)} subject allocation(s) changed." if changes
                 else f"Triggered by: {self.trigger}. The plan is unchanged.",
                 details or ["No allocation changed."])
        return changes

    # ---------------------------------------------------------------
    def result(self, changes: list[dict]) -> dict:
        fields = ["id", "name", "status", "daysLeft", "remainingHours", "weeklyDemand", "priorityWeight",
                  "difficultyWeight", "urgencyWeight", "score", "allocatedHours", "coverage"]
        return {
            "generatedFor": self.today.isoformat(),
            "days": [d.isoformat() for d in self.days],
            "capacity": self.capacity,
            "summary": {
                "availableHours": self.available,
                "allocatedHours": self.allocated,
                "weeklyDemand": self.weekly_demand,
                "workloadRatio": self.workload_ratio,
                "topSubject": self.top["name"] if self.top else None,
                "topScore": self.top["score"] if self.top else 0,
                "conflictCount": len(self.conflicts),
                "sessionCount": len(self.sessions),
            },
            "subjects": [{k: s.get(k, 0) for k in fields} for s in sorted(self.subjects, key=lambda s: -s.get("score", 0))],
            "sessions": self.sessions,
            "conflicts": self.conflicts,
            "steps": self.steps,
            "changes": changes,
        }


def main() -> None:
    request = json.load(sys.stdin)
    json.dump(StudyPlanningAgent(request).run(), sys.stdout)


if __name__ == "__main__":
    main()
