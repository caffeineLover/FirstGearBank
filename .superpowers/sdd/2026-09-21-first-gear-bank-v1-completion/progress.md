# SDD ledger — plan: docs/superpowers/plans/2026-09-21-first-gear-bank-v1-completion.md

Ruling: Execute in the existing shared workspace — the user approved direct implementation here and prohibited commits;
an isolated worktree would hide the approved uncommitted plan set — cost if wrong: changes remain mixed in the current
working tree and must be reviewed by path.

Ruling: Omit TDD, all automated/manual execution, builds, and task commits — explicit user instructions and AGENTS.md
override the execution skill's normal gates — cost if wrong: compilation and runtime defects can only be found during the
user's later manual build and checklist run.

Ruling: Perform final self-review without a subagent — the user did not authorize delegation and the active collaboration
rules forbid spawning one — cost if wrong: review lacks an independent second context.

Pre-flight: Max preview produces an exact ordinary withdrawal consumed by the existing settlement path; no interface
conflict.
Pre-flight: Statement preview data is consumed by printing and the item renderer; one shared bounded DTO will be used.
Pre-flight: Recovery audit persistence is consumed by both registry and settlement commands; recovery methods publish it
atomically with their target state.
Pre-flight: Natural storage/source identity feeds discovery, site selection, lifecycle, and unified decommission; one
durable source row owns every transition.
Pre-flight: Shared topology is produced before Charter and natural lifecycles and consumed by both spacing checks.

Implemented: exact server-side withdrawal Max, immutable one-per-conversation printed statements, generic scrap/fuel/
compost conversion, privileged registry export/restore/reset, explicit settlement findings with durable audit, shared
branch topology, deterministic vanilla-trader discovery, persisted candidate cursor, 7x7 schematic placement phases,
public natural claims, immediate Banker homes, player-visit backfill, and unified decommission administration.

Static review: checked the supported 1.22.7 reference source for generated-structure code shape, terrain-map height,
schematic rotation origin, Cuboidi exclusivity, claim APIs, held-item tooltip signatures, and referenced vanilla assets.
Corrected trader suffix recognition, surface offset, bottom-center rotation, exclusive claim maxima, claim verification,
vanilla-only domain filtering, visit-driven backfill, persisted deferred-search progress, and missing-block rejection.

Documentation: updated README/subsystem responsibility notes and created `docs/manual-v1-acceptance-checklist.md`.

Verification intentionally omitted: no compiler, parser, formatter, test, game, package, commit, or deployment command
was run. Build and runtime status remain unknown until the user performs the checklist.
