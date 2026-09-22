# Banking Max and Printed Statements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> Do not build, test, commit, package, or deploy.

**Goal:** Add server-resolved Physical Max withdrawals and one atomic printed statement per Banker conversation, including
readable statement items, scraps, fuel behavior, and composting.

**Architecture:** Max remains an exact preview followed by the existing ordinary withdrawal settlement, so confirmation
never grants partial fulfillment.  Printing uses a separate session-scoped idempotency record and one locked inventory
plan that consumes paper and inserts the immutable statement together; statement contents are produced from the existing
core projection and stored only as bounded item attributes.

**Tech Stack:** Existing core statement projection, Vintage Story inventories/items/GUI, existing request protocol, and
JSON assets/recipes.

**Spec:** `docs/superpowers/specs/2026-09-08-first-gear-bank-v1-design.md`, sections 10.1 and 13.

## Global Constraints

- Apply the coordinating plan's global constraints.
- Rusty physical units use a 250,000-unit quantum; temporal units use a 1,000,000-unit quantum.
- An explicit amount remains all-or-nothing; Max is a distinct request and never changes explicit-amount semantics.
- A statement has no unique financial authority or redemption value and contains no internal player UID.
- A successful print consumes exactly one sheet of paper and the conversation's single print allowance.

## Review Focus

- Full inventories, partially filled denomination stacks, and mixed rusty/temporal holdings must produce the exact maximum.
- A vanished paper slot or newly full output inventory after preview must leave both paper and allowance untouched.
- Retried identical print packets and changed-payload retries must not duplicate an item.
- Printed transfer rows must contain frozen display names, never internal identities.
- Crafted scraps must erase statement attributes and stack with all other statement scraps.

---

### Task 1: Exact Physical Max Preview

**Files:**
- Modify: `First Gear Bank/src/server/InventorySettlement.cs`
- Modify: `First Gear Bank/src/server/FirstGearBankServer.cs`
- Modify: `First Gear Bank/src/server/BankingProtocol.cs`
- Modify: `First Gear Bank/src/client/BankingClient.cs`
- Modify: `First Gear Bank/src/client/BankingDialog.cs`
- Modify: `First Gear Bank/src/client/BankingDisplay.cs`
- Modify: `First Gear Bank/assets/firstgearbank/lang/en.json`

**Interfaces:**
- Produce: `InventorySettlement.MaximumWithdrawalUnits(IServerPlayer, IWorldAccessor, Currency, long availableUnits)`.
- Produce: request action `previewWithdrawMax` and a `PhysicalBankingPreview` whose committed action remains `withdraw`.
- Consume: existing `InventorySettlement.Prepare(...)` for final exact settlement.

- [ ] Add a read-only inventory-capacity calculation under `inventoryGate`.  Enumerate the same eligible player
  inventories, slot ordering, denomination assets, maximum stack sizes, and serialization rules used by `Prepare`.
  Return `min(account balance rounded down to quantum, exact insertable value rounded down to quantum)` and reject zero.
- [ ] Keep the scan side-effect free: clone candidate stacks, account for merging before empty-slot insertion, use checked
  arithmetic, and dispose no live inventory object.
- [ ] Dispatch `previewWithdrawMax` only for a valid conversation and existing account after ordinary accrual.  Return the
  exact normalized amount and units in the same preview DTO used by explicit withdrawals.
- [ ] Make the client confirmation submit an ordinary exact `withdraw` request.  If slots or balance change before commit,
  the existing settlement rejects the whole amount and the UI instructs the player to request Max again.
- [ ] Add a Max control beside the withdrawal amount without changing deposit controls or permitting client-calculated
  amounts.  Disable it while another request is pending.
- [ ] Update file overviews that currently say Max is unimplemented and add localized labels/errors for Max and stale
  capacity.
- [ ] Review the calculation against empty inventory, merge-only capacity, multiple empty slots, rusty quarter stacks,
  temporal whole stacks, account balances below one quantum, and arithmetic limits.  Do not execute it.

### Task 2: Conversation-Scoped Print Authority

**Files:**
- Modify: `First Gear Bank/src/server/BankerSessions.cs`
- Modify: `First Gear Bank/src/server/BankingProtocol.cs`
- Modify: `First Gear Bank/src/server/FirstGearBankServer.cs`
- Create: `First Gear Bank/src/server/StatementPrinting.cs`

**Interfaces:**
- Replace `BankerConversation` with fields for `PrintToken`, `PrintPrepared`, `PrintCompleted`, request digest, and cached
  terminal reply while retaining player/entity/scope/expiry.
- Produce: `StatementPrintPreview PreviewPrint(IServerPlayer player, Guid scope)`.
- Produce: `StatementPrintResult Print(IServerPlayer player, Guid scope, Guid token, string digest)`.
- Consume: `BankingCoordinator.GetStatement(...)` and `CoreOptions.PrintedRecentTransactions`.

- [ ] Add `previewPrintStatement`, which verifies the session and account, advances/materializes financial state through
  the existing statement path, freezes a bounded print projection, and issues one opaque token owned by the conversation.
- [ ] Define a versioned `PrintedStatementData` record containing heading, canonical holder name, issued world-calendar
  time, financial time/basis, both balances, period aggregates, current rates, CD rows, configured recent history rows,
  frozen transfer display names, and the historical-values notice.  Exclude UIDs, session IDs, and authority tokens.
- [ ] In `StatementPrinting`, preflight one paper removal and one statement insertion while holding `inventoryGate`.
  Apply both slot changes as a single reversible plan using the same before/after fingerprints and callback checks as
  monetary settlement.  A failed preflight or rollback does not mark the allowance used.
- [ ] Bind print confirmation to the token and digest.  Cache the completed result in the conversation so an identical
  retry returns the same terminal response, while token mismatch or changed payload fails closed.
- [ ] Mark `PrintCompleted` only after both slot mutations apply.  Close/disconnect/change-Banker/unavailable paths discard
  all print state; opening a new conversation grants a fresh allowance.
- [ ] Keep printing outside the financial journal because it changes no balance, while logging one bounded `INFO` record
  after success and a `WARN` only for rollback uncertainty.
- [ ] Review every exit path to prove it cannot consume paper alone, insert output alone, or consume the allowance on a
  failed print.  Do not execute it.

### Task 3: Print UI and Immutable Statement Item

**Files:**
- Modify: `First Gear Bank/src/client/BankingClient.cs`
- Modify: `First Gear Bank/src/client/BankingDialog.cs`
- Modify: `First Gear Bank/src/client/BankingDisplay.cs`
- Create: `First Gear Bank/src/client/PrintedStatementDialog.cs`
- Create: `First Gear Bank/src/server/PrintedStatementItem.cs`
- Modify: `First Gear Bank/First Gear BankModSystem.cs`
- Create: `First Gear Bank/assets/firstgearbank/itemtypes/bank-statement.json`
- Create: `First Gear Bank/assets/firstgearbank/itemtypes/statement-scrap.json`
- Create: `First Gear Bank/assets/firstgearbank/recipes/grid/statement-scrap.json`
- Create: `First Gear Bank/assets/firstgearbank/recipes/barrel/statement-scrap-compost.json`
- Modify: `First Gear Bank/assets/firstgearbank/lang/en.json`

**Interfaces:**
- Produce: item class registration `FirstGearBankPrintedStatement`.
- Produce: `PrintedStatementItem.Read(ItemStack)` returning validated display-only `PrintedStatementData`.
- Produce: client confirmation actions `previewPrintStatement` and `printStatement`.

- [ ] Register the custom item class on both sides.  Store the print schema version and bounded scalar/list fields in the
  output stack's attributes, set stack size to one, and reject malformed or oversized attributes as unreadable.
- [ ] Render bearer-readable contents from item attributes in a read-only native dialog on client interaction.  Escape all
  player-controlled names and paginate rows without querying the server or current account state.
- [ ] Add a Print button to the statement page.  Show the server-frozen summary before confirmation and explain the
  one-success-per-conversation rule; failed previews retain the allowance.
- [ ] Define statement and scrap assets with ignition temperature 600 C and burn duration 5 real seconds.  Give the
  statement max stack size one and the generic scrap an ordinary stack size.
- [ ] Add a shapeless one-statement-to-one-scrap grid recipe that emits a fresh generic stack with no copied attributes.
- [ ] Add a sealed barrel recipe requiring exactly 16 scraps for 480 in-game hours and producing exactly one vanilla
  compost block/item.  Add no reverse-paper or whole-compost recipe.
- [ ] Add localized item names, descriptions, print labels, confirmation text, unreadable-data feedback, and historical
  notice copy.
- [ ] Review item registration, attribute bounds, recipe namespace/codes, fuel units, and all dialog close paths.  Do not
  parse assets or launch the game.
