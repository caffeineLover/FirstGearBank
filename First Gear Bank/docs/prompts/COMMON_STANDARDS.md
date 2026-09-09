# Common Coding Standards

## Code Quality
- Prefer clear, maintainable code over cleverness; avoid unnecessary abstractions.
- Keep responsibilities focused, control flow explicit, and side effects visible.
- Preserve existing behavior unless the task explicitly requests a change.
- Keep changes focused; avoid unrelated cleanup or reformatting.
- Use the project's configured formatter, linter, analyzer, compiler, and test tools.

---

## Understand Before Implementing
- Before adding features or changing behavior:

1. Inspect relevant code, documentation, configuration, and recent history.  Ask about material gaps or ambiguities;
    do not invent requirements.

2. Explain what will change, how it will work, and why.  For nontrivial work, compare alternatives and justify your recommendation.

3. Wait for the user's explicit design approval.

4. Create a separate implementation plan covering components, ordered steps, risks, and validation. Only then code.

- Scale detail to the task.
- If discoveries invalidate approved assumptions or materially change the design, pause, explain, and obtain approval
   for the revision before continuing.




---

# 3. Systematic Debugging

Prefer systematic investigation over guessing.  For bugs, failed tests, and unexpected behavior, identify the root cause
instead of repeatedly changing code until the symptom disappears.

1. Reproduce the failure reliably.  If it is intermittent, gather more evidence rather than guessing.
2. Examine errors, logs, inputs, outputs, recent changes, and relevant state.
3. Trace the failure backward until its root cause is understood.
4. State a specific hypothesis that explains the evidence.
5. Test one hypothesis at a time using the smallest possible change.
6. Fix the root cause and add a regression test when practical.
7. Verify the original failure is resolved and existing behavior still works.
8. Add appropriate validation, diagnostics, and defense-in-depth checks to prevent recurrence.

If a hypothesis fails, remove or revert its experimental change, return to the evidence, and form a new hypothesis.  Do not
stack speculative changes or treat disappearance of the symptom as proof of a fix.

---

# 4. Configuration

Prefer configuration files over hard-coded values for behavior that users, operators, or deployments may reasonably need
to change without modifying code.  This includes feature switches, thresholds, paths, schedules, limits, integration
settings, and other operational behavior.

Use YAML for new human-edited configuration unless the project already has an established format, an external system
requires another format, or YAML would introduce an unreasonable dependency.  Do not migrate an existing configuration
format without explicit approval.

Configuration must:

- Centralize and document defaults
- Document fields, units, allowed values, and important interactions
- Validate required fields, types, ranges, and combinations
- Report invalid values with actionable, field-specific errors
- Reject invalid explicit configuration rather than silently replacing it with defaults
- Use safe parsing and never deserialize arbitrary executable types
- Preserve backward compatibility or provide a deliberate migration when its schema changes

Do not expose structural invariants, non-negotiable security guarantees, or every implementation constant merely to make
the code more configurable.  Every option adds documentation, testing, and compatibility obligations.

---

# 5. Human-Readable Source Documentation

Use the documentation syntax required by the applicable language profile.  Document relevant:

- Purpose, responsibility, and larger feature context
- Callers, callees, services, APIs, and architectural relationships
- State changes, side effects, ownership, lifetime, and persistence
- Assumptions, invariants, validation, and authority boundaries
- Domain rules, constraints, workarounds, and API limitations
- Reasons for unusual algorithms, ordering, filtering, or performance decisions
- Failure behavior and responsibilities deliberately left elsewhere

Explain meaning and reasoning rather than restating names, signatures, types, assignments, or visible syntax.  Do not add
fluff or narrate individual statements.

Human-readable explanatory prose stored with source code must use exactly two spaces after a period that ends a sentence
when another sentence follows on the same physical line.  This includes comments, docstrings, XML documentation, Doxygen
documentation, and module or procedure headers.  Do not add trailing spaces when the next sentence starts on a new line.
This rule does not alter user-facing strings, serialized data, protocol text, or generated output.

Documentation lines must not exceed 120 characters, including indentation and comment markers.  If the next word would
cross that limit, move it to the next documentation line.  The two spaces between sentences count toward the limit.

Whenever behavior changes, review all related documentation and update or remove every affected claim in the same change.
Never preserve documentation merely because it already exists.

---

# 6. Logging

Every project must route runtime logs to files under a `logs/` directory at the project or application runtime root.  Create
the directory when it does not exist.  Applications may additionally emit logs to the console, operating system, or another
configured sink, but those destinations do not replace the required files under `logs/`.

Reusable libraries must emit through a caller-provided or project-standard logging abstraction so the host application can
route their records into `logs/`.  Libraries must not silently create unrelated log destinations.

Every emitted record must use one of these exact, case-sensitive severity labels:

- `INFO`: Normal lifecycle events and significant successful operations
- `DEBG`: Diagnostic detail used to understand execution and troubleshoot behavior
- `WARN`: Unexpected or degraded behavior from which the operation can recover
- `CRIT`: A failure that prevents a required operation, threatens data integrity, or requires immediate attention

Use `DEBG`, not `DEBUG`, and `CRIT`, not `ERROR`, `FATAL`, or `CRITICAL`, in formatted log output.  Map native framework
levels to these four labels when necessary.

Each record must include a timestamp, severity, component or source, and message.  Include relevant identifiers and exception
details when they help diagnose the event.  Never log credentials, secrets, tokens, or sensitive data.  Keep generated log
files out of version control and configure rotation or retention appropriate to the project.

---

# 7. Completion

Before considering a change complete, run the relevant formatter, static checks, build or compile step, and tests available
in the project.  Report checks that could not be run and the reason.
