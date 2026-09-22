# Common Coding Standards
Standards version: 1.0.0

## Code Quality
- Prefer simple, readable code over unnecessary abstraction.
- Preserve existing behavior and avoid unrelated changes, cleanup, or reformatting.

## Before Implementing
- Inspect the relevant code before changing it.
- Ask only when a material ambiguity or missing decision prevents a safe implementation.
- If new information materially changes the requested design, stop and ask before proceeding.

## Debugging
- Find and fix the root cause, not just the symptom.
- Remove unsuccessful speculative changes, verify the fix when practical, and say clearly what could not be verified.

## Configuration
- Use the existing configuration format; otherwise prefer YAML.
- Do not migrate formats without approval.
- Validate values enough to prevent crashes or clearly invalid behavior, and avoid unnecessary options.

## Human-Readable Source Documentation
- Document non-obvious purpose, reasoning, constraints, and important behavior.
- Do not narrate self-explanatory code or add documentation merely for completeness.
- Explain the function’s purpose and role clearly, without repeating the implementation or becoming verbose.
- Update affected documentation when behavior changes.
- In source documentation, use two spaces after a sentence-ending period when another sentence follows on the same line.
- Keep documentation lines at 120 characters or fewer.

## Logging
- When logging applies, use the project's existing facilities and conventions rather than creating a parallel system.
- Log useful diagnostic context without excessive noise, and never log credentials, secrets, or sensitive data.
- Reusable libraries should use the host application's logging abstraction when available.
