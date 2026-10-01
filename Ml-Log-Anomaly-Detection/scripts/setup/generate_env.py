"""Create .env from .env.example with random secrets (never overwrites an existing .env unless --force).

    python scripts/setup/generate_env.py
"""

from __future__ import annotations

import argparse
import secrets
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()
    target = ROOT / ".env"
    if target.exists() and not args.force:
        print(f"{target} already exists (use --force to regenerate).")
        return 0
    lines = []
    for line in (ROOT / ".env.example").read_text(encoding="utf-8").splitlines():
        if "=" in line and not line.startswith("#") and "CHANGE_ME" in line:
            key = line.split("=", 1)[0]
            length = 48 if "KEY" in key else 24
            line = f"{key}={secrets.token_urlsafe(length)}"
        lines.append(line)
    target.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Wrote {target}. Demo passwords are in DEMO_ENGINEER_PASSWORD / DEMO_ADMIN_PASSWORD.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
