# Project Hygiene Standards

Standards version: 0.3.0

These standards apply to every project unless the user or project-specific instructions explicitly override them.

## Repository Layout and Documentation

Preserve an existing repository's coherent structure unless the user requests reorganization.  For new projects,
follow ecosystem conventions; when none apply, use root `src/` for primary source and `docs/` for documentation.  Group
strongly related files in clearly named directories.

Keep documentation under `docs/` except for conventional root files such as `README.md`, `LICENSE`, `CONTRIBUTING.md`,
agent instructions, and repository configuration.

Every project must have a root `AGENTS.md`.  Reference shared standards and skills instead of duplicating them, using
portable or explicitly documented references.

A maintained project's root `README.md` must describe its purpose, prerequisites, setup, use, and validation commands.

## Git

Use Git unless the user opts out.  Before initializing, verify the intended boundary and ensure the project is not
inside another worktree.  Initialize Git before making project changes.

Include a root `.gitignore`, `.gitattributes` for line endings and binary files, and `.editorconfig` for cross-editor
formatting.

Inspect Git status before and after changes.  Preserve unrelated work and stage or commit only the intended coherent
scope.  If the user requests a commit without providing a message, write a concise, descriptive message that accurately
summarizes the change.  If the intended commit scope is unclear, ask before staging or committing.

Do not rewrite published history, move or reuse published tags, or force-push shared branches without explicit user
authorization.

Follow the repository's branch policy.  If none exists, ask about creating a branch before significant features,
refactors, or intrusive work.  Routine maintenance may remain on the current branch unless instructed otherwise.

## Reproducibility and Dependencies

A project must be buildable, testable, and usable as applicable from a clean clone using its documented setup.  Do not
depend on untracked files, IDE state, undocumented machine paths, or undocumented external resources.

Document required tools and how to obtain them.  Research resources may remain external, but build and runtime inputs
must be reproducibly available.

Commit dependency manifests and lock files when supported.  Do not commit downloaded dependency caches.

## Secrets and Local Configuration

Never commit secrets or private machine configuration, including credentials, tokens, signing keys, personal paths,
private environment files, and machine-specific settings.

For configuration that may contain private values, commit a sanitized template by appending `.template` to the complete
live filename, such as `config.yaml.template` for `config.yaml`.  Ignore the exact live path.  Preserve structure and
safe defaults, use unmistakable placeholders, and document setup.

Prefer environment variables or an approved secrets manager over plaintext files for production credentials.

## Generated Files and Protected Documents

Do not commit disposable build output, release packages, logs, caches, or temporary files.  Ignore applicable paths and
patterns such as `bin/`, `obj/`, `dist/`, `releases/`, `logs/`, and `*.zip`.

Commit generated files only when they are intentional inputs or deliverables, such as migrations, designer-managed
files, consumed clients, or source assets.  Document exceptions not established by project convention.

A coding agent may read user-maintained `.docx` files when needed but must not modify, regenerate, reformat, rename,
move, or delete them without explicit instructions.

## Documentation
When creating substantial documentation, write the canonical source in Markdown under `docs/`. Use Pandoc to generate a same-basename LaTeX file, then compile the LaTeX into a same-basename PDF with XeLaTeX. Always edit the Markdown and regenerate the other formats; never edit generated LaTeX or PDF files directly. Verify the PDF for formatting problems. Commit the Markdown and LaTeX files, but keep the generated PDF uncommitted. If Pandoc or XeLaTeX is unavailable, preserve the Markdown and report the missing dependency.