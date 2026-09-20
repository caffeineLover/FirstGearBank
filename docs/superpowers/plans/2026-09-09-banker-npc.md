# Banker NPC Implementation Plan

**Goal:** Supply the approved custom Banker entity and server-owned home, spawning, and replacement lifecycle.

**Architecture:** `BankerEntity` inherits vanilla `EntityDressedHumanoid`, not any trading class.  A server
`BankerLifecycle` owns a separately versioned world-bound roster, supplied home interiors, spawn identities, and
replacement timers.  A native AI task returns displaced Bankers to their deterministic safe home; idle behavior
does not select other buildings.  The existing ledger and session validator retain all financial authority.

**Spec:** v03 sections 14, 15.7–15.8, and 17.  Existing client and server adapters are the approved integration boundary.
**Stack:** .NET 10, Vintage Story 1.22.7 API plus installed VSEssentials/VSSurvivalMod assemblies, no Harmony.

- [x] Add entity/AI registration, a custom dressed humanoid with banking-only right-click and no currency drops,
  and `assets/firstgearbank/entities/humanoid/banker.json` referencing verified vanilla outfit/animation assets.
- [x] Add persistent Banker homes and assignment reconciliation.  Reserve spawn identity before entity creation;
  preserve assigned unloaded entities, distinguish death from removal, and quarantine contradictory evidence.
- [x] Sample replacement duration once from current configured 3–7-day defaults in an independent salted domain.
  Persist remaining ordinary-calendar days; only safe loaded homes may spawn, without force-loading terrain.
- [x] Expose trusted home establishment/removal/departure methods for later branch content and a permission-gated
  `/bankernpc place` / `/bankernpc remove reason` command for administrator placement.  Manual homes grant no claims
  or Charter/natural-branch status.  Charter validation, first-arrival scheduling, topology protection, and worldgen
  remain separate content work.  Detaching an assigned Banker supports the specified 17:00 cutoff.
- [x] Wire startup, entity events, saves, shutdown, current replacement configuration, localization, and README.
- [x] Release-build the full solution; inspect lifecycle/API/asset references, privacy, comments, and whitespace.
  Request a focused read-only source review.  No tests, commits, deployment, or claims of in-game verification.

Verified 1.22.7 sources: `EntityDressedHumanoid` supports fixed `outfit` metadata without a trade inventory;
`AiTaskRegistry` accepts custom `AiTaskBase` tasks; `WaypointsTraverser` uses dimension-aware internal-Y targets;
`OnEntityDeath` is distinct from `OnEntityDespawn`; `IsFullyLoadedChunk` and collision checks avoid terrain loading.
Save staging is not an atomic entity/world disk transaction.  Missing assigned entities never authorize replacement.
