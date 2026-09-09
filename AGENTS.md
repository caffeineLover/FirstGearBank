# Working instructions

Read `First Gear Bank/docs/prompts/COMMON_STANDARDS.md`, `CSHARP_STANDARDS.md`, and
`PROJECT_HYGIENE.md` for source conventions.

The user explicitly requires no test suite.  Do not create test projects, test dependencies, or a TDD workflow.
Use a Release build and focused source review for verification.  Keep implementation and documentation economical.
The banking core belongs in `First Gear Bank/src/core` and must have no Vintage Story dependency.
The v03 specification defines behavior; the user's latest instructions override older delivery-plan process requirements.

Use descriptive responsibility-based filenames.  Every core type needs a substantive comment, including records and
interfaces.  File overviews must explain purpose, collaborators, units, state ownership, and relevant limitations;
method comments must explain intent and invariants rather than merely repeat their names.

Never use XML summary opening or closing tags in source comments; retain the descriptive prose without wrappers.
Leave exactly one blank line between internal sealed record declarations, before the next record's attached comment.
