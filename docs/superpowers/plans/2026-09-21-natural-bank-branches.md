# Natural Bank Branches Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> Do not build, test, commit, package, or deploy.

**Goal:** Deterministically create, protect, staff, reconcile, backfill, and decommission natural First Gear Bank branches
near eligible vanilla trader structures.

**Architecture:** A dedicated natural-branch registry owns stable source dispositions and placement state.  Worldgen and
visit-driven backfill feed the same deterministic evaluator; it reserves durable intent before world mutation, uses one
7x7 schematic transformed into four rotations, and reconciles each placement phase idempotently.  A shared topology view
prevents overlap with Charter branches without merging their lifecycle state.

**Tech Stack:** Vintage Story generated-structure metadata, chunk-column callbacks, `BlockSchematic`, `LandClaim`, mod-data
persistence, existing Banker lifecycle, and YAML settings.

**Spec:** `docs/superpowers/specs/2026-09-08-first-gear-bank-v1-design.md`, sections 16 and 17.

## Global Constraints

- Apply the coordinating plan's global constraints.
- A source is a vanilla generated trader structure, never a trader entity.
- One source receives one stable hash roll and one persisted disposition; configuration changes do not reroll it.
- Backfill is false by default, visit-driven, bounded to loaded data, and never performs a global scan or force-load.
- Placement must not remove claims, protected blocks, block entities, player construction, or incompatible terrain.

## Review Focus

- Repeated discovery from overlapping chunks/players must converge on one source row and one branch.
- Unavailable neighboring chunk/region data must retain `AwaitingData`, not become a permanent skip.
- Every candidate failure must be deterministic, and exhausted search must become a permanent `Skipped` disposition.
- Reservation, schematic, claim, anchor, and Banker evidence must distinguish resumable partial work from ambiguity.
- Decommission must stop banking/protection/replacement while leaving accounts and placed blocks intact.

---

### Task 1: Shared Branch Topology Boundary

**Files:**
- Create: `First Gear Bank/src/server/BranchTopologyIndex.cs`
- Modify: `First Gear Bank/src/server/CharterLifecycle.cs`
- Modify: `First Gear Bank/src/server/CharterRegistryStorage.cs`

**Interfaces:**
- Produce: `BranchFootprint(Guid Branch, BranchKind Kind, Cuboidi Bounds, bool Active)`.
- Produce: `BranchTopologyIndex.Replace(BranchKind kind, IEnumerable<BranchFootprint> footprints)`.
- Produce: `bool BranchTopologyIndex.Conflicts(Cuboidi candidate, int spacing, Guid? exceptBranch = null)`.

- [ ] Implement a server-thread in-memory spatial authority that retains only immutable footprints supplied by durable
  lifecycle owners.  It performs dimension-aware intersection and horizontal spacing checks with checked coordinates.
- [ ] Publish Charter footprints after registry load, activation, retirement, and disposal staging.  Keep Charter's exact
  protected-cell enforcement and registry format unchanged.
- [ ] Replace Charter's private cross-branch spacing scan with the shared index while retaining same-placement exclusions.
- [ ] Ensure the index never creates, retires, persists, or repairs branches; it is only a combined conflict view.
- [ ] Review active/removed transitions, dimension handling, and boundary-distance inclusivity.  Do not run the game.

### Task 2: Natural Source and Branch Persistence

**Files:**
- Create: `First Gear Bank/src/server/NaturalBranchStorage.cs`
- Create: `First Gear Bank/src/server/NaturalBranchIdentity.cs`

**Interfaces:**
- Produce: `NaturalSourceDisposition` values `AwaitingData`, `NotSelected`, `Reserved`, `Placed`, `Skipped`, and
  `Decommissioned`.
- Produce: immutable `NaturalBranchSource`, `NaturalBranchReservation`, and `NaturalBranchRegistryFile` records.
- Produce: `NaturalBranchIdentity.SourceId(worldId, dimension, structureCode, bounds, generationSalt)`.

- [ ] Derive source IDs from exact world binding, dimension, canonical vanilla structure code, exact bounds, and a fixed
  versioned salt using SHA-256.  Derive selection roll, candidate ordering, rotation, and branch GUID from separate domain
  labels so adding later random decisions cannot perturb earlier ones.
- [ ] Persist source structure facts, original probability, roll outcome, disposition, selected candidate/rotation,
  schematic version, branch GUID, placement phase, claim evidence, anchor evidence, and decommission reason.
- [ ] Validate version/world/checksum, unique source and branch IDs, legal disposition transitions, coordinate bounds, and
  phase/evidence combinations.  Preserve unreadable bytes and disable natural management rather than recreating state.
- [ ] Store a complete registry snapshot under a dedicated versioned world-data key and stage after every disposition or
  placement-phase transition.
- [ ] Review determinism inputs, schema bounds, partial-state combinations, and corrupt-state behavior.  Do not serialize
  sample data.

### Task 3: Vanilla Trader Discovery and Deterministic Selection

**Files:**
- Create: `First Gear Bank/src/server/NaturalBranchDiscovery.cs`
- Modify: `First Gear Bank/src/server/ServerConfiguration.cs`
- Modify: `First Gear Bank/assets/firstgearbank/config/configlib-patches.json`
- Modify: `First Gear Bank/assets/firstgearbank/lang/en.json`

**Interfaces:**
- Produce: `bool NaturalBranchDiscovery.TryRecognize(GeneratedStructure structure, out NaturalStructureSource source)`.
- Produce: worldgen feed and loaded-visit feed into `NaturalBranchLifecycle.Observe(source, DiscoveryMode mode)`.

- [ ] Build an explicit allowlist from canonical vanilla trader structure codes/groups present in the supported 1.22.7
  assets.  Require the `game` domain and reject modded aliases, missing bounds, and entity-only trader evidence.
- [ ] Subscribe to a late chunk-column worldgen pass and inspect generated-structure metadata already available for that
  column.  Queue registry mutation onto the server thread if the callback is on a worldgen worker.
- [ ] For backfill, react only to ordinary connected-player movement into newly visited loaded chunk columns.  Use bounded
  `WalkStructures` calls over loaded nearby bounds and never ask the world manager to load/generate a chunk or region.
- [ ] On first observation, persist the deterministic probability and disposition before any site search.  Existing rows
  ignore later probability changes; unselected rows never reroll.
- [ ] Keep `AwaitingData` when a candidate decision needs unavailable loaded-world evidence and retry only after an
  ordinary later discovery/visit exposes it.
- [ ] Preserve current YAML defaults and Config Lib labels for probability/backfill; reload affects only unevaluated
  sources.
- [ ] Review source allowlisting, thread handoff, duplicate observations, probability endpoints 0/1, and no-force-load
  behavior.  Do not generate terrain.

### Task 4: Candidate Search and Schematic Asset

**Files:**
- Create: `First Gear Bank/src/server/NaturalBranchSiteSelector.cs`
- Create: `First Gear Bank/src/server/NaturalBranchSchematic.cs`
- Create: `First Gear Bank/assets/firstgearbank/worldgen/schematics/natural-bank-v1.json`
- Create: `First Gear Bank/assets/firstgearbank/blocktypes/natural-bank-anchor.json`
- Create: `First Gear Bank/assets/firstgearbank/blocktypes/natural-bank-strongbox.json`
- Create: `First Gear Bank/src/server/NaturalBranchBlocks.cs`
- Modify: `First Gear Bank/First Gear BankModSystem.cs`

**Interfaces:**
- Produce: `SiteSearchResult Find(NaturalStructureSource source, string sourceId)` with `Ready`, `AwaitingData`, or
  `Exhausted` and exact candidate/rotation/bounds.
- Produce: `NaturalBranchSchematic.Place(NaturalBranchReservation reservation)` using `EnumReplaceMode.Replaceable`.
- Produce registered classes `FirstGearBankNaturalAnchor` and `FirstGearBankStrongbox`.

- [ ] Generate candidate origins in a stable hash order for the inclusive 20-to-50-block horizontal band measured from
  the trader structure boundary.  Evaluate all four rotations consistently and require the 7x7 footprint to fit one
  chunk column.
- [ ] Reject candidates intersecting any land claim, engine-protected region, branch spacing envelope, generated
  structure, block entity, nonreplaceable/player-placed block, inadequate support, insufficient clearance, liquid, or
  unsafe/incompatible terrain.  Treat unavailable evidence separately from a definite rejection.
- [ ] Choose floor height from deterministic terrain samples and orient the front door toward the nearest point on the
  source bounds with stable tie-breaking.
- [ ] Create one 7x7 exterior/5x5 interior schematic with floor, walls, roof, door, light, table, chair, flowerpot/flower,
  a locked decorative non-inventory strongbox, a hidden anchor, and a Banker standing/spawn marker.  Encode no Charter.
- [ ] Load and validate the schematic once, clone packed data before rotation, and reject missing/remapped required blocks
  before reserving a site.  Placement may replace only verified replaceable cells and must not bulldoze around the
  footprint.
- [ ] Make anchor and strongbox uncraftable.  The strongbox exposes no inventory; the hidden anchor stores only branch/
  schematic identity needed for reconciliation and is not financial authority.
- [ ] Review exact distances, chunk boundaries, rotation transforms, replace mode, and required furnishing markers.  Do
  not load or place the schematic.

### Task 5: Reservation, Placement, Claim, Staffing, and Reconciliation

**Files:**
- Create: `First Gear Bank/src/server/NaturalBranchLifecycle.cs`
- Modify: `First Gear Bank/src/server/BankerLifecycle.cs`
- Modify: `First Gear Bank/src/server/BranchTopologyIndex.cs`
- Modify: `First Gear Bank/First Gear BankModSystem.cs`

**Interfaces:**
- Produce: `NaturalBranchLifecycle.Observe(NaturalStructureSource source, DiscoveryMode mode)`.
- Consume: `BankerLifecycle.EstablishHome(...)`, `BranchTopologyIndex`, `NaturalBranchStorage`, and the schematic placer.

- [ ] Persist `Reserved` with exact candidate and all deterministic IDs before the first block mutation.
- [ ] Advance separately persisted phases for schematic blocks, anchor identity, public-use/traverse land claim, Banker
  home, and final `Placed` disposition.  Stage each phase before starting the next side effect.
- [ ] Create a dedicated `LandClaim` covering the branch footprint with pedantic build/break protection,
  `AllowTraverseEveryone = true`, and `AllowUseEveryone = true`.  Use mod-owned identity/description and retain enough
  evidence to find only this claim during reconciliation/decommission.
- [ ] Establish an immediately staffed home using the schematic standing/interior bounds and existing Banker ownership,
  session registration, death, return-home, and replacement behavior.
- [ ] On startup and ordinary loaded-chunk arrival, reconcile each nonterminal reservation.  Matching evidence advances
  the phase; absent evidence resumes the next idempotent step; conflicting/duplicate evidence quarantines that row and
  logs `CRIT` without deleting blocks, entities, or claims.
- [ ] Publish a topology footprint as soon as reservation is durable, preventing Charter/natural races.  Remove it only
  when a durable decommission transition says the branch is inactive.
- [ ] Dispose callbacks and stage state without decommissioning or killing a loaded Banker during shutdown.
- [ ] Review the state machine at every crash boundary and every duplicate callback path.  Do not simulate crashes.

### Task 6: Unified Natural Decommission Administration

**Files:**
- Modify: `First Gear Bank/src/server/CharterLifecycle.cs`
- Modify: `First Gear Bank/src/server/NaturalBranchLifecycle.cs`
- Modify: `First Gear Bank/src/server/BankerLifecycle.cs`
- Modify: `First Gear Bank/assets/firstgearbank/lang/en.json`
- Modify: `README.md`

**Interfaces:**
- Produce: one `/bankbranch decommission <reason>` router that resolves the administrator's current position against
  active Charter and natural branch footprints.

- [ ] Require `controlserver`, exactly one containing active branch, and a bounded nonblank reason.  Reject overlapping or
  ambiguous registry evidence without choosing one.
- [ ] Preserve current Charter retirement behavior.  For natural branches, persist `Decommissioned`, close conversations,
  stop Banker replacement, unregister/retire the managed Banker home, remove only the mod-owned land claim, and retain all
  building blocks and financial accounts.
- [ ] Make replay idempotent after a crash between registry staging and claim/NPC cleanup.  Never remove a player claim or
  a claim whose durable identity does not match the branch.
- [ ] Update command help, diagnostics, and README administration text for both branch kinds.
- [ ] Review containment boundaries, reason persistence, claim identity, session closure, and restart replay.  Do not run
  the command.
