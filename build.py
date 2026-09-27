#!/usr/bin/env python3

# Repository-level entry point for building, deploying, and packaging First Gear Bank.  Cake owns JSON validation,
# compilation, staging, packaging, and deployment.  This wrapper provides the public build interface and also supports
# optional semantic version bumps and Git commits.
#
# Usage:
#
#   python build.py
#   python build.py deploy
#   python build.py package
#   python build.py package --bump=z
#   python build.py package --bump=y --commit=auto
#   python build.py package --bump=x --commit="My commit message"

import argparse
import subprocess
import tempfile
from pathlib import Path

root = Path(__file__).parent.resolve()


parser = argparse.ArgumentParser(
    description="Build, deploy, or package First Gear Bank.",
    epilog="""Examples:

  python build.py
  python build.py deploy
  python build.py package
  python build.py deploy --bump=z
  python build.py package --bump=z
  python build.py package --bump=z --commit=auto

With no action specified, the mod is built but is neither deployed nor packaged.
""",
    formatter_class=argparse.RawDescriptionHelpFormatter,
)


parser.add_argument(
    "action",
    nargs="?",
    choices=["build", "deploy", "package"],
    default="build",
    help="build the mod, deploy it for local testing, or create a release package",
)

parser.add_argument(
    "--bump",
    choices=["x", "y", "z"],
    help="bump one semantic-version component before building",
)

parser.add_argument(
    "--commit",
    help="stage and commit the worktree after a successful build; use 'auto' for a Codex-written message",
)

args, cake_args = parser.parse_known_args()

if any(arg == "--target" or arg.startswith("--target=") for arg in cake_args):
    parser.error("Use 'build', 'deploy', or 'package' instead of --target.")

target = {
    "build": "Build",
    "deploy": "Deploy",
    "package": "Package",
}[args.action]



# Translate the public action to a Cake target; pass other unrecognized options through to Cake.

command = [
    "dotnet", "run",
    "--project", "CakeBuild/CakeBuild.csproj",
    "--",
    f"--target={target}",
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
