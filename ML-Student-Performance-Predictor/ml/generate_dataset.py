"""Generate the synthetic student dataset used to train the model.

Each student has two hidden traits — academic ability and motivation — that drive
the observable features. The final exam score is a weighted mix of the features
plus random noise, so the relationship is realistic but learnable.

Usage:  python ml/generate_dataset.py [--rows 320] [--seed 42]
Output: ml/data/students.csv
"""
import argparse
from pathlib import Path

import numpy as np
import pandas as pd

FIRST_NAMES = [
    "Aisha", "Omar", "Fatima", "Bilal", "Zainab", "Hamza", "Maryam", "Usman", "Hira", "Ali",
    "Emma", "Liam", "Sophia", "Noah", "Olivia", "Lucas", "Mia", "Ethan", "Ava", "Leon",
    "Sara", "Daniel", "Lena", "Jonas", "Nora", "Elias", "Amira", "Yusuf", "Layla", "Adam",
    "Hannah", "Felix", "Chloe", "Ibrahim", "Ines", "Mateo", "Priya", "Arjun", "Mei", "Kenji",
]
LAST_NAMES = [
    "Khan", "Ahmed", "Malik", "Hussain", "Raza", "Schmidt", "Müller", "Weber", "Fischer", "Wagner",
    "Smith", "Johnson", "Brown", "Garcia", "Martin", "Rossi", "Silva", "Nowak", "Kowalski", "Yilmaz",
    "Sharma", "Patel", "Chen", "Wang", "Tanaka", "Haddad", "Rahman", "Iqbal", "Becker", "Hoffmann",
]
PROGRAMMES = ["Computer Science", "Data Science", "Software Engineering", "Information Systems", "Applied Mathematics"]

# True relationship used to generate the target (the model has to rediscover it).
TRUE_WEIGHTS = {
    "attendance": 0.20,
    "study_hours": 0.85,
    "assignment_avg": 0.25,
    "quiz_avg": 0.18,
    "previous_exam": 0.30,
}
TRUE_INTERCEPT = -5.0
NOISE_SD = 4.5


def generate(rows: int, seed: int) -> pd.DataFrame:
    rng = np.random.default_rng(seed)
    ability = rng.normal(0, 1, rows)
    motivation = rng.normal(0, 1, rows)

    attendance = np.clip(82 + 8 * motivation + rng.normal(0, 6, rows), 40, 100)
    study_hours = np.clip(10 + 4 * motivation + 1.0 * ability + rng.normal(0, 2.5, rows), 1, 30)
    assignment = np.clip(72 + 9 * ability + 5 * motivation + rng.normal(0, 6, rows), 30, 100)
    quiz = np.clip(68 + 11 * ability + 2 * motivation + rng.normal(0, 7, rows), 25, 100)
    previous = np.clip(65 + 12 * ability + rng.normal(0, 8, rows), 20, 100)

    final = (
        TRUE_INTERCEPT
        + TRUE_WEIGHTS["attendance"] * attendance
        + TRUE_WEIGHTS["study_hours"] * study_hours
        + TRUE_WEIGHTS["assignment_avg"] * assignment
        + TRUE_WEIGHTS["quiz_avg"] * quiz
        + TRUE_WEIGHTS["previous_exam"] * previous
        + rng.normal(0, NOISE_SD, rows)
    )
    final = np.clip(final, 0, 100)

    names = [f"{rng.choice(FIRST_NAMES)} {rng.choice(LAST_NAMES)}" for _ in range(rows)]
    return pd.DataFrame({
        "student_id": [f"STU-{2024000 + i + 1}" for i in range(rows)],
        "name": names,
        "programme": rng.choice(PROGRAMMES, rows),
        "attendance": attendance.round(1),
        "study_hours": study_hours.round(1),
        "assignment_avg": assignment.round(1),
        "quiz_avg": quiz.round(1),
        "previous_exam": previous.round(1),
        "final_score": final.round(1),
    })


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rows", type=int, default=320)
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()

    df = generate(args.rows, args.seed)
    out = Path(__file__).parent / "data" / "students.csv"
    out.parent.mkdir(parents=True, exist_ok=True)
    df.to_csv(out, index=False, encoding="utf-8")
    print(f"Wrote {len(df)} students to {out}")
    print(df.describe().round(2).to_string())


if __name__ == "__main__":
    main()
