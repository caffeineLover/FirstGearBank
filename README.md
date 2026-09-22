# First Gear Bank

Vintage Story 1.22.7 banking mod under development.  The engine-independent .NET 10 library is in
`First Gear Bank/src/core`; the game adapter is in `First Gear Bank/src/server`, and the native banking interface
is in `First Gear Bank/src/client`.

Build the core with .NET SDK 10:

```powershell
dotnet build "First Gear Bank/src/core/FirstGearBank.Core.csproj" -c Release
```

Build the mod with `VINTAGE_STORY` pointing to the installed 1.22.7 game directory:

```powershell
dotnet build "First Gear Bank/First Gear Bank.csproj" -c Release
```

The mod references the installed `VintagestoryAPI.dll`, `VintagestoryLib.dll`, `Lib/protobuf-net.dll`,
`Mods/VSEssentials.dll`, and `Mods/VSSurvivalMod.dll` without bundling game assemblies.  The concrete calendar exposes
the named sleep modifier for runtime interest; the standard game mods supply native humanoid rendering and AI.

There is no test suite, per the user's instruction.  Build success is not gameplay verification.

The core contains six-decimal money, financial clocks, deterministic CIR/Constant rates, monthly market history,
lazy savings/vault accrual, an immutable double-entry journal, serialized requests, name-based transfers,
administrative corrections and recovery audit, CD quotes/contracts/maturities, notification state, statements, and
framed persistence.

`InterestRates.cs` defines the interest-rate models, their economic settings, monthly history, and CD yield calculations.
`FinanceEngine.cs` applies those stored rates to account accrual and coordinates financial boundaries and CD maturities.
Each core source file documents its role, state ownership, units, and host integration boundaries.

Supply `IBankingHost` for authenticated identities, valid Banker sessions, current time, permission checks, and locked
inventory changes.  `Advance` processes global time before request handling; `Execute` handles sequenced mutations.
`Create` is for a confirmed first installation only.  `Restore` validates existing bytes and replays monetary projections.
Preserve failed load bytes and never create a replacement bank when loading existing authority fails.
Call `Save` for an immutable serialized snapshot and stage it through the game save API.  Serialization is not disk durability.

`IExactLiquidityIndex` is a required dependency for the specification's economically accrued CD funding denominator.
The user has approved `ScanningLiquidityIndex` as the server default, accepting account-count-dependent checkpoint cost.
It projects each account separately at financial-month boundaries and funding-changing transactions.  The resulting
liquidity target is cached between checkpoints; ordinary ticks and quotes do not scan unless due events require catch-up.
Balance caps, per-account rounding, interest, and CD pricing are unchanged.  A custom exact index can still be supplied.

The server adapter implements lifecycle/save staging, YAML loading, bounded file diagnostics,
trusted calendar/runtime readings, authenticated player observations, Banker conversation validation, bounded network
dispatch, administrator corrections, personal-inventory preflight/rollback, and acknowledged notification delivery.
It waits until `RunGame` to restore financial-clock anchors and stages authority during disposal before the final engine save.

Configuration is `<game data>/ModConfig/firstgearbank.yaml`.  The mod creates a documented YAML file on first start and
does not read JSONC.  Existing YAML files are never rewritten by load or reload.  Exact liquidity scans are automatic at
financial-month and funding-changing checkpoints; no opt-in or custom index is required to start banking.

Administrators with `controlserver` can use `/bankadmin status`, `/bankadmin reload`, and
`/bankadmin correct name Rusty|Temporal signedAmount reason`.  Corrections require an authenticated player and an
existing target account.  Trusted server integrations can also call `CorrectBalance` with a related correction ID.
Registry recovery commands are `registry export`, `registry restore filename reason`, and `registry reset reason`;
artifacts are kept beneath the server data directory at `ModData/FirstGearBank/registry-recovery`. A reset exports the
quarantined bytes before creating a new empty name epoch. Ambiguous physical settlements require the explicit command
`/bankadmin settlement resolve id InventoryApplied|InventoryNotApplied reason`; neither outcome is inferred from money.
Config Lib remains optional.  When present, it discovers the bundled settings definition and edits the same YAML file;
after saving either there or in a text editor, use `/bankadmin reload` to apply the settings.

`BankerLifecycle` now manages the custom `firstgearbank:banker` entity, persistent homes, and death/replacement state.
It reuses vanilla humanoid art and navigation, not a trader inventory or shopping cart.  Living Bankers idle at a safe
central workstation, look around, can flee damage, and prioritize returning to safe interior space.  Death drops nothing
and changes no customer money.
Replacement waits use the configured `BankerReplacement` range (default 3–7 ordinary in-game days), sampled once and
saved independently of financial time.  Unloading never starts replacement; ambiguous saved/entity evidence disables
the affected home instead of spawning another Banker.  Safe placement never force-loads terrain or alters blocks.

Administrators with `controlserver` can look at nearby clear solid ground and run `/bankernpc place`.  Move clear of
the standing position if staffing is delayed by occupancy.  Look at the NPC and run `/bankernpc remove reason` to
retire its home without replacement.  These manual public access points grant no land claim, Charter status, spacing
reservation, or account privilege.

The craftable `firstgearbank:banker-charter` is a stateless wall plaque.  Its shaped recipe is `F C F / F P F / F G F`:
firewood, charcoal, parchment, and a non-consumed rusty gear.  A placed plaque activates only in one enclosed native
room with 5–13-block interior width and length, 2–13-block height, a complete supported 5×5 standing footprint, one
door, exactly one Charter, a table or desk, chair or stool, artificial light, ordinary storage, and a potted flower.
The placer must have ordinary build permission over the exact captured interior, shell, dependencies, and furnishings.

Activation persists immutable exact-position protection, the configured same-dimension spacing reservation (default
32 blocks), and one sampled first-arrival wait (default 2–5 ordinary days).  Doors, storage, seats, lights, the Banker,
and Charter removal remain usable; player structural changes and mutation tools are denied.  Environmental damage and
direct third-party world writes are not blocked or repaired.  Before the first NPC is assigned, changed premises pause
the saved remaining wait and staffing gate; repair resumes the same wait without rerolling it.

The original placer can sneak-right-click the Charter to remove and collect it; current build access is rechecked.
Removal immediately releases topology and spacing.  An assigned Banker remains available only until the saved displayed
17:00 cutoff; an unstaffed arrival is cancelled.  A replacement plaque creates a new branch and wait.
Administrators with `controlserver` may stand inside one accepted branch and use `/bankbranch decommission reason` to
release it immediately while leaving blocks and all finance unchanged. The same command handles natural branches and
removes only the matching mod-owned claim and Banker home. Compatible furnishing blocks may opt in with
the boolean attributes `firstgearbankTable`, `firstgearbankSeat`, `firstgearbankArtificialLight`, or
`firstgearbankFurniture` (floor-footprint occupancy only); unsupported custom multi-position mutation behavior cannot
be protected through public APIs.

The lifecycle calls `Server.RegisterBanker` and `UnregisterBanker` on the server thread with an execution-time
availability predicate.  Sessions enforce same-dimension six-block reach and a five-minute idle timeout.
Only the authenticated player's own hotbar and backpack participate in banking inventory changes.
Registration sets a replicated interaction hint: right-clicking that Banker opens the native personal ledger.
Content with its own interaction handling can instead call `Client.OpenBanker(entityId)` on the mod system's client.
The hint grants no authority; the server still checks the registered entity, session, distance, and availability.
Session expiry, removal, or branch cutoff sends a scoped close notification to invalidate the displayed conversation.
The native quarter item and four-quarters-to-one-whole recipe are included; the quarter currently reuses vanilla gear art.
Unverified external quarter integrations are not accepted or selected automatically.

Natural branches use vanilla generated-trader structure metadata, never trader entities. On first observation each
supported vanilla trader location receives one deterministic, persisted probability result. A selected source searches
stable 7×7 candidates 20–50 blocks from the trader bounds without force-loading terrain; definite rejections advance a
persisted cursor, while unavailable candidate data waits for an ordinary visit. The chosen schematic includes a door,
light, table, chair, potted flower, decorative locked non-inventory strongbox, hidden reconciliation anchor, public-use
and public-traverse protection, and an immediately established Banker home. It contains no Charter. Configuration
defaults to probability `0.15` and existing-world backfill `false`; reload affects only sources not yet evaluated.
Backfill, when enabled, observes only loaded columns newly entered by online players. Reservations share Charter spacing,
do not reroll, and retain blocks and accounts after decommissioning.

The `firstgearbank-v1` channel registers `BankingPacket`, carrying UTF-8 JSON with PascalCase fields.  Requests contain
`Action`, `Scope`, `Sequence`, and action-specific intent fields; they never carry an account UID.  Supported actions are
`open`, `close`, `statement`, `names`, `previewDeposit`, `previewWithdraw`, `previewWithdrawMax`,
`previewPrintStatement`, `printStatement`, `confirmTransfer`, `quoteCd`,
`deposit`, `withdraw`, `transfer`, `buyCd`, and
`acknowledge`.  Recognized monetary mutations use the core's sequenced retry contract.  Incoming JSON is limited to four
KiB, pages to fifty entries, and queued requests to four per connection and 256 globally. Withdrawal Max is resolved by
the server against both the exact account balance and current personal-inventory capacity; it never trusts a client-side
estimate.

The client ledger contains account, cash, transfer, CD, history, and cumulative-total screens with localized Banker
framing.  Every money operation has a server-resolved confirmation; cash previews do not reserve funds or inventory.
The account screen can print one immutable, nonstacking bearer-readable statement per Banker conversation by exchanging
one parchment for a frozen snapshot. A statement includes exact balances, configured recent history, cumulative totals,
CDs, rates, issue time, and a historical-data warning. Crafting removes its private payload into generic statement scrap;
statements and scraps burn, and sixteen scraps seal into one compost after 480 hours.
Known-name discovery follows the server mode.  Quotes show exact payoff and all pricing components, and active CDs
distinguish nonspendable value, financial duration, and projected calendar maturity from runtime-based maturity.
Read requests use negative correlation sequences; mutations use positive core sequences.  Only one request is pending
at a time.  After five real seconds, Retry resends the identical mutation packet rather than issuing another payment.
Closing a pending transaction does not cancel it; reopening resumes its pending status.  Check history before repeating
an operation whose conversation became unavailable.  Notice acknowledgments follow chat submission and receipt-cache
persistence.  A bounded cache retains 2,048 notice IDs per opaque world/player namespace under
`<game data>/Cache/firstgearbank`; it stores no names, UIDs, message contents, or balances.  A crash between display
and cache persistence can still repeat a notice; this is not a guarantee of exactly-once visible delivery.

The newly completed Max, printed-statement, recovery-administration, and natural-branch paths have not been built or run
as part of this implementation pass. NPC, branch, claim, inventory, and financial save staging are not one atomic disk
transaction. Durable phase records and explicit recovery commands handle recognized ambiguity conservatively, but an
administrator must inspect evidence and choose a settlement finding; the mod never guesses.

Server-only receipts are co-serialized with 1.22.7 personal inventories and checked against successful bank settlement
tombstones on login.  Missing or divergent evidence and pending settlements quarantine that player's banking access.
This is conservative detection, not automatic hard-crash repair; no item or money replay is guessed. Quarantined
settlements retain their manifest and frozen planned records for the authenticated recovery command. The library and
adapter do not promise atomic disk writes across game inventory and world-save blobs.
Player-facing responses use names; `Snapshot`, journal records, and persisted sections are privileged server-only data.
