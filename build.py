#!/usr/bin/env python3

# This repository-level entry point coordinates Cake builds, optional semantic version bumps, and optional Git commits
# for First Gear Bank.  Cake owns JSON validation, the Release build, mod packaging, and deployment; without an explicit
# target, its Default task replaces the deployed mod under StoryForge's working_test_world installation.  Arguments this
# wrapper does not recognize pass through to Cake, including its Deploy and ZipModFolder targets.  When --commit is
# used, the script stages the entire worktree only after Cake succeeds; --commit=auto asks an ephemeral, read-only Codex
# process to describe that staged diff.  A failed subprocess stops the workflow, but the script does not undo completed
# deployment or staging side effects.
#
# Usage:
#
#   python build.py
#   python build.py --bump=z
#   python build.py --bump=y --commit=auto
#   python build.py --bump=x --commit="My commit message"
#   python build.py --target=Deploy
#   python build.py --target=ZipModFolder
#

import argparse
import subprocess
import tempfile
from pathlib import Path

root = Path(__file__).parent.resolve()

parser = argparse.ArgumentParser(
    description="Build and deploy First Gear Bank through the repository's Cake workflow.",
    epilog="""Cake options not recognized by this wrapper pass through unchanged.

Examples:

  python build.py
  python build.py --bump=z
  python build.py --bump=y --commit=auto
  python build.py --bump=x --commit="My commit message"
  python build.py --target=Deploy
  python build.py --target=ZipModFolder

With no explicit target, Cake builds, packages, and deploys the mod to the StoryForge working test world.
""",
    formatter_class=argparse.RawDescriptionHelpFormatter,
)
parser.add_argument("--bump", choices=["x", "y", "z"], help="bump one semantic-version component before building")
parser.add_argument(
    "--commit",
    help="stage and commit the worktree after a successful build; use 'auto' for a Codex-written message",
)
args, cake_args = parser.parse_known_args()

# Cake receives the version bump in its native argument format and every unrecognized option verbatim.  This preserves
# access to Cake targets without forcing this wrapper to duplicate Cake's build, packaging, or deployment policy.

command = [
    "dotnet", "run",
    "--project", "CakeBuild/CakeBuild.csproj",
    "--"
]

if args.bump:
    command.append(f"--bump={args.bump}")

command += cake_args

subprocess.run(command, cwd=root, check=True)

# Committing is deliberately opt-in because staging captures every current worktree change, including edits made outside
# this script.  A build-only invocation exits before making any Git changes.
if args.commit is None:
    raise SystemExit(0)

subprocess.run(["git", "add", "-A"], cwd=root, check=True)

# An empty staged diff is a successful no-op, so Git commit is not invoked with an invalid empty change set.
result = subprocess.run(
    ["git", "diff", "--cached", "--quiet"],
    cwd=root
)

if result.returncode == 0:
    print("Nothing to commit.")
    raise SystemExit(0)

message = args.commit

# Automatic messages are based only on the staged diff that Git would commit.  The temporary file carries the final
# Codex response across the Windows command boundary and is removed whether message generation succeeds or fails.
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

# This point is reached only after a successful build and a confirmed nonempty staged diff.
subprocess.run(
    ["git", "commit", "-m", message],
    cwd=root,
    check=True
)
