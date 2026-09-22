# First Gear Bank v1 Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement these plans in order.
> The user explicitly prohibited subagent delegation, builds, automated tests, commits, packaging, and deployment.

**Goal:** Complete the remaining v1 player features, recovery administration, and deterministic natural branches without
changing the established banking, Charter, or Banker behavior.

**Architecture:** Four subplans isolate inventory/UI work, privileged recovery, natural-world lifecycle, and final
integration.  Existing core and server authorities remain authoritative; new engine-facing state is persisted in
separately validated records, and no client or world asset becomes financial authority.

**Tech Stack:** C#/.NET 8, Vintage Story 1.22.7 public API, JSON/JSONC game assets, YAML configuration, immutable core
records, and the existing `firstgearbank-v1` bounded JSON-over-Protobuf channel.

**Spec:** `docs/superpowers/specs/2026-09-08-first-gear-bank-v1-design.md`

## Global Constraints

- Do not build, run any automated or in-game test, launch Vintage Story, package, deploy, commit, or push.
- Do not create a test project, test dependency, test fixture, or TDD workflow.
- Preserve unrelated user changes and do not rewrite existing behavior outside the approved v1 completion scope.
- Keep the banking core under `First Gear Bank/src/core` free of Vintage Story dependencies.
- Use only Vintage Story 1.22.7 public APIs; do not introduce Harmony.
- Follow all source-comment and three-blank-line conventions in `First Gear Bank/docs/prompts`.
- Use YAML for mod configuration and retain the current optional Config Lib asset integration.
- Do not introduce legacy JSONC configuration migration.
- Never infer a cross-save ordering guarantee between world state and player inventory data.
- Exact liquidity scans remain approved at month and funding checkpoints.
- Implementation verification is limited to focused source review.  The final handoff supplies a player-run manual
  checklist and clearly states that Codex did not build or test the changes.

## Ordered Subplans

1. `2026-09-21-banking-max-and-printed-statements.md`
2. `2026-09-21-bank-recovery-administration.md`
3. `2026-09-21-natural-bank-branches.md`
4. `2026-09-21-v1-integration-and-manual-checklist.md`

The first two subplans can be implemented independently but both modify the request host, so execute them serially.
Natural branches depend on the existing Banker lifecycle rather than either financial feature.  Integration follows all
three and owns only composition, documentation consistency, and the manual checklist.

## Review Focus

- Inventory changes between a Max preview and confirmation must reject the exact request, never silently lower it.
- A lost statement-print reply must not consume a second paper or create a second statement in the same conversation.
- Registry and settlement repair must reject stale, wrong-world, malformed, or incompletely reasoned administrator input.
- Natural-branch discovery must never reroll a recorded source or force-load terrain during backfill.
- A crash at any natural-branch reservation/placement/claim/staffing boundary must reconcile without a duplicate building,
  Banker, or claim.

---

This coordinating plan does not authorize changes beyond the four linked subplans.  If implementation discovers that a
required Vintage Story public hook is absent or behaves incompatibly with the approved design, stop at that boundary and
return the evidence for a design revision instead of narrowing behavior.
