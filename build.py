#!/usr/bin/env python3

# Usage:
#   python build.py
#   python build.py --bump=z
#   python build.py --bump=y --commit=auto
#   python build.py --bump=x --commit="My commit message"
#   python build.py --target=ZipModFolder

import argparse
import subprocess
import tempfile
from pathlib import Path

root = Path(__file__).parent.resolve()

parser = argparse.ArgumentParser()
parser.add_argument("--bump", choices=["x", "y", "z"])
parser.add_argument("--commit")
args, cake_args = parser.parse_known_args()

# ------------------------------------------------------------
# Build
# ------------------------------------------------------------

command = [
    "dotnet", "run",
    "--project", "CakeBuild/CakeBuild.csproj",
    "--"
]

if args.bump:
    command.append(f"--bump={args.bump}")

command += cake_args

subprocess.run(command, cwd=root, check=True)

# ------------------------------------------------------------
# Optional commit
# ------------------------------------------------------------

if args.commit is None:
    raise SystemExit(0)

subprocess.run(["git", "add", "-A"], cwd=root, check=True)

# Nothing to commit?
result = subprocess.run(
    ["git", "diff", "--cached", "--quiet"],
    cwd=root
)

if result.returncode == 0:
    print("Nothing to commit.")
    raise SystemExit(0)

message = args.commit

# ------------------------------------------------------------
# Let Codex write the commit message
# ------------------------------------------------------------

if message.lower() == "auto":

    diff = subprocess.run(
        ["git", "diff", "--cached"],
        cwd=root,
        capture_output=True,
        text=True,
        check=True
    ).stdout

    prompt = f"""
Write a concise one-line Git commit message describing these staged changes.

Output ONLY the commit message. No quotes, Markdown, or explanation.

{diff}
"""

    with tempfile.NamedTemporaryFile(delete=False) as f:
        output_file = Path(f.name)

    try:
        subprocess.run(
            [
                "cmd", "/c",
                "codex", "exec",
                "--ephemeral",
                "--sandbox", "read-only",
                "--output-last-message", str(output_file),
                "-"
            ],
            cwd=root,
            input=prompt,
            text=True,
            check=True
        )

        message = output_file.read_text().strip()

    finally:
        output_file.unlink(missing_ok=True)

    if not message or len(message.splitlines()) != 1:
        raise RuntimeError(
            "Codex did not return a one-line commit message."
        )

    print(f"Commit message: {message}")

# ------------------------------------------------------------
# Commit
# ------------------------------------------------------------

subprocess.run(
    ["git", "commit", "-m", message],
    cwd=root,
    check=True
)