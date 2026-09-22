# V1 Integration and Manual Checklist Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> Do not build, test, commit, package, or deploy.

**Goal:** Finish composition/documentation consistency and hand the user a complete, ordered manual acceptance checklist
without executing any verification.

**Architecture:** This plan adds no new domain behavior.  It wires already implemented services in explicit ownership
order, reconciles documentation/configuration claims with source, performs static source review only, and records every
manual scenario the user must execute after building and deploying independently.

**Tech Stack:** Existing C# composition root, JSON/YAML assets, Markdown documentation, and Git read-only inspection.

**Spec:** `docs/superpowers/specs/2026-09-08-first-gear-bank-v1-design.md`, sections 22, 23.4, 23.6-23.9, and 26.

## Global Constraints

- Apply the coordinating plan's global constraints.
- Do not change `modinfo.json` version unless the user separately requests a version bump.
- Do not create a package or copy files to a game/test-world directory.
- The final response must distinguish implemented-but-unverified work from user-verified behavior.

## Review Focus

- Startup and disposal ordering must not leave callbacks using disposed banking, topology, or lifecycle services.
- Reloaded settings must reach natural discovery without altering persisted rolls or existing branch state.
- README and source overviews must not still claim completed features are missing.
- Every new item/block/recipe/class code must match its registration and localization key exactly.
- The manual checklist must cover ordinary, failure, retry, restart, multiplayer, and administrative paths.

---

### Task 1: Composition and Documentation Consistency

**Files:**
- Modify: `First Gear Bank/First Gear BankModSystem.cs`
- Modify: `First Gear Bank/src/server/FirstGearBankServer.cs`
- Modify: `First Gear Bank/src/server/readme.txt`
- Modify: `First Gear Bank/src/client/readme.txt`
- Modify: `First Gear Bank/src/core/readme.txt`
- Modify: `README.md`
- Modify: `First Gear Bank/assets/firstgearbank/lang/en.json`

**Interfaces:**
- Consume: all services and registrations produced by the preceding three subplans.

- [ ] Construct the shared topology index before Charter/natural lifecycles, then construct storage/discovery/natural
  lifecycle after the financial server and Banker lifecycle are available.
- [ ] Dispose discovery callbacks first, then natural and Charter lifecycles, Banker lifecycle, financial server, and
  client resources.  Null public references after disposal.
- [ ] Route configuration reload notifications to natural lifecycle only for future unevaluated sources; printing reads
  the current core option when a new preview is created.
- [ ] Update file overviews and subsystem readmes for actual responsibility boundaries, especially statements, Max,
  recovery, topology, worldgen, and decommissioning.
- [ ] Replace README's unfinished-feature warning with accurate setup/use/admin/recovery/natural-generation limitations.
  Retain the explicit cross-save atomicity limitation and quarantine recovery requirements.
- [ ] Reconcile every new asset code, class registration, action string, DTO field, command spelling, configuration key,
  and localization key by focused source review.  Do not invoke compilers, parsers, formatters, or the game.

### Task 2: Final Static Review and User-Run Manual Checklist

**Files:**
- Create: `docs/manual-v1-acceptance-checklist.md`
- Review only: all files changed by the four completion plans

**Interfaces:**
- Produce: the checklist linked in the final response.

- [ ] Write prerequisites explaining that the user must build/deploy, back up the world, enable useful server logging,
  and use disposable test worlds for crash-boundary checks.
- [ ] Cover explicit deposits/withdrawals, rusty/temporal denomination rules, Max with empty/partial/full inventory, stale
  confirmation, insufficient funds, and retry behavior.
- [ ] Cover live statements, print preview/confirmation, no-account/no-paper/full-inventory failures, one success per
  conversation, reconnect/new-Banker allowance, bearer reading, nonstacking, scrap conversion, fuel, and 480-hour compost.
- [ ] Cover registry export, wrong-world/malformed restore rejection, valid restore, audited reset, connected-only name
  observation, and preservation of personal banking during registry quarantine.
- [ ] Cover settlement resolution for deposit/withdrawal with both findings, repeated-command rejection, audit visibility,
  and post-resolution access.
- [ ] Cover new-world deterministic trader selection, 0/1 probability endpoints, one branch per source, distance,
  orientation, furnishing, immediate Banker, protection/use/travel, spacing, unsuitable sites, and no rerolls.
- [ ] Cover backfill false/true, visit-driven evaluation, no force-loading/global scan, modded/player-spawned trader
  exclusion, deferred data, and permanent exhausted-site skip.
- [ ] Cover restart/crash checkpoints after reservation, blocks, anchor, claim, and Banker; require no duplicate structure,
  claim, home, or NPC after recovery.
- [ ] Cover natural and Charter decommission, mandatory reasons, session cutoff, replacement cutoff, claim cleanup,
  preserved buildings/accounts, and restart idempotency.
- [ ] Cover multiplayer isolation, two players using one Banker, session distance/dimension/timeout, notification behavior,
  and basic load observation around multiple generated traders.
- [ ] Perform a final read-only `git diff --check` equivalent by visually reviewing the diff only; the user's prohibition
  means no command-based verification is run.  Record in the handoff that build and runtime status are unknown.
