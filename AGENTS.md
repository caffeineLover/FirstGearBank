# Working instructions

## Read 
    - `First Gear Bank/docs/prompts/COMMON_STANDARDS.md`
    - `First Gear Bank/docs/prompts/CSHARP_STANDARDS.md`
    - `First Gear Bank/docs/prompts/VINTAGE_STORY_STANDARDS.md`
    - `First Gear Bank/docs/prompts/PROJECT_HYGIENE.md`


## Project scope
This is a hobby Vintage Story mod, not commercial or safety-critical software.
    - Prefer simple, readable solutions for realistic gameplay.
    - Do not add enterprise-style architecture, exhaustive edge-case handling, crash-recovery machinery
    - Do not make automated test suites, test projects, test dependencies, or a TDD workflow.
    - Keep changes focused. Fix the requested problem and stop.
    - Manual verification should cover normal gameplay and a few realistic failures, not contrived or unlikely scenarios.
    - When in doubt, optimize for the user's time and token budget.

## Architecture
    - The banking core belongs in `First Gear Bank/src/core` and must have no Vintage Story dependency.
    - The v03 specification defines behavior; the user's latest instructions override older delivery-plan process requirements.
