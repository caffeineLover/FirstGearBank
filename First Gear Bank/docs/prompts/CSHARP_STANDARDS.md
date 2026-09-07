# C# Coding Standards

Standards version: 0.1.1

This profile applies to all C# source, including Vintage Story mods.

---

# 1. Type Safety and Tooling

Honor nullable-reference-type annotations and configured compiler or analyzer warnings.  Avoid `dynamic`, unsafe code, and
warning suppressions unless an external API or measured requirement makes them necessary; document the reason and keep the
scope as narrow as possible.

Use the repository's configured C# formatter and analyzers except where this profile explicitly requires the three blank
lines surrounding callables.

---

# 2. File-Level Comments

Every source file must begin with a high-level `/* ... */` block comment before `using` directives, namespace or type
declarations, and all other code.

The block must explain the file's responsibility and role rather than repeat its filename or type names.  Include whichever
details are relevant: subsystem, framework or engine integrations, lifecycle, ownership or persistence, file-wide design
decisions, and excluded responsibilities.

---

# 3. Callable Comments

Every named function, method, constructor, local function, operator overload, event handler, callback, and other callable
member must be preceded by a descriptive comment block.

After any indentation, every line in the block must begin with exactly four forward slashes (`////`).  Blank comment lines
must contain exactly `////`.  The block's final line must be exactly `////` and must appear immediately before the
declaration, with no completely blank line between them.

Include whichever information from the common documentation standard explains the callable's role.  Very small callables
still require a callable comment, but they may omit internal comments when their entire body performs one obvious operation.

Constructor comments must explain relevant initialization, dependencies, ownership, and lifecycle rather than merely state
that an object is created.  Lifecycle methods, event handlers, and callbacks must identify when and by whom they are invoked.
For frequently invoked or performance-sensitive callbacks, explain why inexpensive checks precede expensive work.

---

# 4. Internal Comments

Comments inside callables must use ordinary `//` markers.  Comment every meaningful phase and non-obvious implementation
decision, including branches, early returns, calculations, workarounds, API limitations, filtering, ordering, invariants,
complex loops, fallbacks, and error paths.

Internal comments normally describe several related statements rather than narrate individual lines.  Use `//` for blank
separators within multi-paragraph internal comments.  Do not use file-level `/* ... */` or callable-level `////` markers
inside a callable.

---

# 5. Required Three-Blank-Line Spacing

Treat each `////` block and the callable it documents as one unit.  A completely blank line contains no spaces or other
characters.

- Place exactly three blank lines after one callable's closing brace and before the next callable's first `////` line.
- Precede the first callable in a file or type with exactly three blank lines, normally after the type's opening brace or
  the last preceding field, property, or other member.
- When a type's closing brace immediately follows its final callable, place exactly three blank lines after the callable's
  closing brace.
- When a non-callable member follows a callable, preserve three blank lines before that member when practical.
- Never place a completely blank line between a callable's final `////` line and its declaration.

Do not allow a formatter or cleanup tool to collapse this spacing.

```csharp
/*
 * Holds and disposes a background-task registration created during startup.
 *
 * This service owns only the registration handle.  Persistent application
 * state remains owned by a separate service.
 */

using System;

public sealed class ExampleService : IDisposable
{
    private readonly IDisposable _registration;



    //// Creates the service and takes ownership of a task registration.
    ////
    //// The startup routine creates the registration and passes its handle
    //// here.  This service must dispose the handle but does not own any
    //// downstream service invoked by the registered callback.
    ////
    public ExampleService(IDisposable registration)
    {
        _registration = registration;
    }



    //// Releases the background-task registration transferred at startup.
    ////
    public void Dispose()
    {
        // Dispose only the registration transferred to this service.  The
        // application retains ownership of the callback's dependencies.
        _registration.Dispose();
    }



}
```

---

# 6. Logging

Use the project's established .NET logging abstraction and configure its file sink to satisfy `COMMON_STANDARDS.md`.  Map
native levels to the exact output labels `INFO`, `DEBG`, `WARN`, and `CRIT`.  `Console.WriteLine`, `Debug.WriteLine`, and
similar diagnostic output do not replace the required logs.
