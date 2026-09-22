# Project Hygiene Standards
Standards version: 1.0.0
These standards apply unless user or project-specific instructions override them.

## Repository Layout
- Preserve a coherent existing repository structure unless reorganization is requested.
- For new projects, follow ecosystem conventions; otherwise use root `src/` and `docs/` directories.
- Every project must have a root `AGENTS.md`; reference shared standards instead of duplicating them.

## Git
- Use Git unless the user opts out; do not initialize a repository inside another repository.
- Include a root `.gitignore`, `.gitattributes`, and `.editorconfig`.
- Preserve unrelated work and modify only intended files.
- Review Git status after making changes.
- Do not stage, commit, or push unless explicitly instructed.
- Do not rewrite history, move tags, or force-push without explicit approval.
- Follow the repository's branch policy; otherwise remain on the current branch unless asked to create one.

### .gitignore
- Ignore generated build/release output, binaries, logs, caches, and temporary files.
- Add project-specific output directories such as `bin/`, `obj/`, `dist/`, `releases/`, and `logs/` when applicable.
- Do not ignore file types globally when they may be intentional source assets.
- Keep binary or generated files in version control only when explicitly required as project inputs.

## Secrets and Local Configuration
- Never commit credentials, tokens, keys, private environment files, or machine-specific configuration.
- For private config files, provide a sanitized `<filename>.template` and ignore the live file.
- Preserve useful defaults and replace private values with obvious placeholders.

## Protected Documents
- Coding agents may read user-maintained `.docx` files when needed.
- Do not modify, regenerate, rename, move, or delete them without explicit instructions.

## Documentation
- Keep project documentation under `docs/`, except conventional root files and repository configuration.
- Keep canonical documentation in Markdown unless the project uses another established format.
- A maintained root `README.md` should describe purpose, prerequisites, setup, use, and validation.