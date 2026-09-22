# C# Coding Standards
Standards version: 1.0.0
This profile applies to all C# source, including Vintage Story mods.

## Type Safety and Tooling
- Respect configured compiler/analyzer warnings.
- Preserve this profile's required callable spacing when using formatters or cleanup tools.

## File-Level Comments
- Every source file must begin with a high-level `/* ... */` comment before all code.
- Explain the file's responsibility, role, and relevant design context, including important integrations or ownership.
- Do not repeat filenames or type names.

## Logging
- Use the project's established logging facilities and follow `COMMON_STANDARDS.md`.

## Callable Comments
- Every named callable must have a descriptive `//` comment block immediately before its declaration.
- Follow `COMMON_STANDARDS.md` and explain its purpose, responsibilities, and relevant context.
- Even trivial callables require a comment, but they need no unnecessary detail.
- Constructors should document meaningful initialization, dependencies, ownership, and lifecycle.
- Lifecycle methods, handlers, and callbacks should state when and by whom they are invoked.

## Internal Comments
- Comment meaningful phases and non-obvious decisions, constraints, workarounds, and errors.
- Comments should explain related code rather than narrate individual statements.

## Required Three-Blank-Line Spacing
- Treat each callable comment and its declaration as one unit.
- Use exactly three completely blank lines before and after each callable unit.
- Never place a blank line between a callable comment and its declaration.
- Do not let formatters or cleanup tools collapse this spacing.
