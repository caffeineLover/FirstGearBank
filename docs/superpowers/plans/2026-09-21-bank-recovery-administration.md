# Bank Recovery Administration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> Do not build, test, commit, package, or deploy.

**Goal:** Provide permission-gated, durable recovery for quarantined recipient registries and ambiguous inventory
settlements without rewriting history or guessing physical state.

**Architecture:** The core exposes narrow atomic recovery methods and persists immutable administrative audit records in
the control section.  The Vintage Story adapter handles permissions, bounded command parsing, connected-player
observation, and server-only recovery files; commands never accept client packets as authority.

**Tech Stack:** Engine-independent immutable core, sectioned persistence, server commands, server data directory, SHA-256
checksums, and existing diagnostics.

**Spec:** `docs/superpowers/specs/2026-09-08-first-gear-bank-v1-design.md`, sections 10.1 and 21.1.

## Global Constraints

- Apply the coordinating plan's global constraints.
- A mandatory nonblank reason is bounded to 512 UTF-8 bytes and stored with authenticated administrator identity/time.
- Recovery adds a revision and audit evidence; it never edits or removes an old journal record.
- Registry recovery observes only authenticated players currently connected after the recovery commit.
- Settlement recovery accepts an exact settlement GUID and one explicit finding: `InventoryApplied` or
  `InventoryNotApplied`.

## Review Focus

- Wrong-world registry snapshots and invalid epoch/revision/name mappings must be rejected before publication.
- A reset must preserve corrupt registry bytes for export even if later healthy saves occur.
- A previously resolved or nonexistent settlement must be unchanged by repeated commands.
- `InventoryApplied` must publish exactly the settlement's predeclared financial records once; `InventoryNotApplied` must
  publish none of them.
- Reasons containing command-like text, whitespace-only input, or excessive UTF-8 must not bypass validation.

---

### Task 1: Durable Administrative Audit Model

**Files:**
- Modify: `First Gear Bank/src/core/BankState.cs`
- Modify: `First Gear Bank/src/core/BankPersistence.cs`
- Create: `First Gear Bank/src/core/RecoveryAdministration.cs`

**Interfaces:**
- Produce: `RecoveryAction` enum with `RegistryRestore`, `RegistryReset`, and `SettlementResolution`.
- Produce: `SettlementFinding` enum with `InventoryApplied` and `InventoryNotApplied`.
- Produce: immutable `RecoveryAuditRecord` with ID, action, administrator, reason, server UTC text, target ID, finding,
  prior revision, and resulting revision.
- Produce: bounded `RecoveryAudit` collection in `BankState` and the control persistence section.

- [ ] Add the recovery enums and record with substantive core comments and no Vintage Story types.
- [ ] Persist audit records in a new control-section schema version with deterministic ordering and strict count/string/
  enum/revision validation.  Migrate the current schema by supplying an empty audit collection.
- [ ] Bound retained audit records without erasing unresolved settlement evidence; use the specification's existing
  persistence limits and reject corrupt input rather than silently truncating it during restore.
- [ ] Add a shared validator for administrator identity, mandatory reason, target identifier, and UTC text before any
  candidate state is built.
- [ ] Review schema dispatch, checksums, migrations, and quarantine behavior for old current-development saves.  Do not
  serialize or load a file.

### Task 2: Registry Export, Verified Restore, and Audited Reset

**Files:**
- Modify: `First Gear Bank/src/core/BankPersistence.cs`
- Modify: `First Gear Bank/src/core/RecoveryAdministration.cs`
- Create: `First Gear Bank/src/server/RegistryRecoveryStorage.cs`
- Modify: `First Gear Bank/src/server/FirstGearBankServer.cs`

**Interfaces:**
- Produce: `RegistryRecoverySnapshot ExportRegistryRecovery()`.
- Produce: `BankResult RestoreRegistryRecovery(RegistryRecoverySnapshot snapshot, RecoveryContext context)`.
- Produce: `BankResult ResetRegistry(RecoveryContext context)`.
- Produce commands `/bankadmin registry export`, `/bankadmin registry restore <filename> <reason>`, and
  `/bankadmin registry reset <reason>`.

- [ ] Define a standalone recovery snapshot with format version, world ID, epoch, registry revision, current entries,
  historical names, source checksum, and export UTC.  Reuse core registry validation before accepting restore input.
- [ ] Export either the healthy registry section or the exact quarantined raw registry bytes to a fixed server-only
  recovery directory beneath the mod data path.  Sanitize filenames, prevent path traversal, and never overwrite an
  existing export.
- [ ] Restore only a valid structured snapshot for the exact current world.  Atomically install its epoch, revision,
  entries, index, and history; clear quarantine bytes only after validation; invalidate all scopes, quotes,
  confirmations, and cached recipient tokens; append one audit record; publish one zero-journal bank revision.
- [ ] Reset by first preserving the corrupt bytes to a unique export, then creating a new nonempty epoch with empty names
  and revision zero.  Invalidate the same transient authority, append the audit record, and publish without changing
  accounts, CDs, notices, settlements, or journal.
- [ ] After a successful restore/reset and persistence staging, call the normal authenticated observation path only for
  `api.World.AllOnlinePlayers` whose connection state is `Playing`.
- [ ] Require `controlserver`, reject recovery while finance authority is unavailable, and return concise command results
  that name the action/revision without exposing UIDs or raw corrupt bytes.
- [ ] Review atomic publication, export-before-reset ordering, same-world validation, token invalidation, and connected-only
  observation.  Do not invoke a command.

### Task 3: Explicit Settlement Resolution

**Files:**
- Modify: `First Gear Bank/src/core/RecoveryAdministration.cs`
- Modify: `First Gear Bank/src/core/BankingCoordinator.cs`
- Modify: `First Gear Bank/src/core/BankPersistence.cs`
- Modify: `First Gear Bank/src/server/InventoryRecoveryReceipts.cs`
- Modify: `First Gear Bank/src/server/FirstGearBankServer.cs`
- Modify: `First Gear Bank/assets/firstgearbank/lang/en.json`

**Interfaces:**
- Produce: `BankResult ResolveSettlement(Guid settlementId, SettlementFinding finding, RecoveryContext context)`.
- Produce command `/bankadmin settlement resolve <settlement-id> <InventoryApplied|InventoryNotApplied> <reason>`.

- [ ] Validate that the target exists, is quarantined, has complete manifest/draft evidence, belongs to one player, and has
  not already been resolved.  Do not inspect live inventory as proof of historical save ordering.
- [ ] For `InventoryApplied`, append the settlement's frozen journal records after verifying their balances, request facts,
  operation IDs, currency, amount, and direction against the settlement.  Recalculate affected projections and rusty
  liquidity through existing core functions without generating replacement operation IDs.
- [ ] For `InventoryNotApplied`, append no financial record and retain the frozen draft solely as recovery evidence.
- [ ] In both cases, mark the settlement finalized with an immutable resolution record, remove the player's adapter
  quarantine only after the core commit, append the audit record, and increment the global revision once.
- [ ] Invalidate the affected player's old scopes/cached responses so no pre-resolution retry can reapply the operation.
  Preserve permanent replay protection for a finding that accepts the operation.
- [ ] Require the exact GUID, exact finding spelling, `controlserver`, and mandatory reason.  Log only settlement ID,
  finding, administrator display name, and revision.
- [ ] Update recovery comments and localized/admin help text that currently say automatic or administrative repair is
  unavailable.
- [ ] Review deposit and withdrawal findings in both directions, replay behavior, liquidity updates, and preservation of
  all prior evidence.  Do not execute recovery.
