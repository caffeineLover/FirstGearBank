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

The mod references the installed `VintagestoryAPI.dll`, `VintagestoryLib.dll`, and `Lib/protobuf-net.dll` without
bundling game assemblies.  The concrete calendar dependency exposes the named sleep modifier for runtime interest.

There is no test suite, per the user's instruction.  Build success is not gameplay verification.

The core contains six-decimal money, financial clocks, deterministic CIR/Constant rates, monthly market history,
lazy savings/vault accrual, an immutable double-entry journal, serialized requests, name-based transfers,
administrative corrections, CD quotes/contracts/maturities, notification state, statements, and framed persistence.

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

The server adapter implements lifecycle/save staging, comment-preserving JSONC loading, bounded file diagnostics,
trusted calendar/runtime readings, authenticated player observations, Banker conversation validation, bounded network
dispatch, administrator corrections, personal-inventory preflight/rollback, and acknowledged notification delivery.
It waits until `RunGame` to restore financial-clock anchors and stages authority during disposal before the final engine save.

Configuration is `<game data>/ModConfig/firstgearbank.jsonc`.  Existing configuration files are never rewritten by load
or reload.  The obsolete `AllowExactLiquidityScans` setting is ignored with a diagnostic, including old `false` values;
no opt-in or custom index is required to start banking.  This change does not clear corruption or recovery quarantines.

Administrators with `controlserver` can use `/bankadmin status`, `/bankadmin reload`, and
`/bankadmin correct name Rusty|Temporal signedAmount reason`.  Corrections require an authenticated player and an
existing target account.  Trusted server integrations can also call `CorrectBalance` with a related correction ID.
Config Lib remains optional; its custom UI adapter is not implemented, so direct JSONC and the reload command are used.

Banker content must call `Server.RegisterBanker(branchId, entity, availabilityPredicate)` on the server thread after
validating and spawning its branch's Banker, and `UnregisterBanker` on removal.  The predicate must include current
branch availability and damage cutoffs.  Sessions enforce same-dimension six-block reach and a five-minute idle timeout.
Only the authenticated player's own hotbar and backpack participate in banking inventory changes.
Registration sets a replicated interaction hint: right-clicking that Banker opens the native personal ledger.
Content with its own interaction handling can instead call `Client.OpenBanker(entityId)` on the mod system's client.
The hint grants no authority; the server still checks the registered entity, session, distance, and availability.
Session expiry, removal, or branch cutoff sends a scoped close notification to invalidate the displayed conversation.
The native quarter item and four-quarters-to-one-whole recipe are included; the quarter currently reuses vanilla gear art.
Unverified external quarter integrations are not accepted or selected automatically.

The `firstgearbank-v1` channel registers `BankingPacket`, carrying UTF-8 JSON with PascalCase fields.  Requests contain
`Action`, `Scope`, `Sequence`, and action-specific intent fields; they never carry an account UID.  Supported actions are
`open`, `close`, `statement`, `names`, `previewDeposit`, `previewWithdraw`, `confirmTransfer`, `quoteCd`,
`deposit`, `withdraw`, `transfer`, `buyCd`, and
`acknowledge`.  Recognized monetary mutations use the core's sequenced retry contract.  Incoming JSON is limited to four
KiB, pages to fifty entries, and queued requests to four per connection and 256 globally.  Physical Max controls,
printing, branch topology/protection, charter/replacement spawning, natural-branch generation,
and registry repair administration remain unfinished; this is not yet a playable full-spec release.

The client ledger contains account, cash, transfer, CD, history, and cumulative-total screens with localized Banker
framing.  Every money operation has a server-resolved confirmation; cash previews do not reserve funds or inventory.
Known-name discovery follows the server mode.  Quotes show exact payoff and all pricing components, and active CDs
distinguish nonspendable value, financial duration, and projected calendar maturity from runtime-based maturity.
Read requests use negative correlation sequences; mutations use positive core sequences.  Only one request is pending
at a time.  After five real seconds, Retry resends the identical mutation packet rather than issuing another payment.
Closing a pending transaction does not cancel it; reopening resumes its pending status.  Check history before repeating
an operation whose conversation became unavailable.  Notice acknowledgments follow chat submission and receipt-cache
persistence.  A bounded cache retains 2,048 notice IDs per opaque world/player namespace under
`<game data>/Cache/firstgearbank`; it stores no names, UIDs, message contents, or balances.  A crash between display
and cache persistence can still repeat a notice; this is not a guarantee of exactly-once visible delivery.

Client UI code has been Release-build/source checked, not verified in a running game.  Banker NPC assets/spawning are
still absent; this client change does not make the entire mod playable without a trusted Banker content integration.

Server-only receipts are co-serialized with 1.22.7 personal inventories and checked against successful bank settlement
tombstones on login.  Missing or divergent evidence and pending settlements quarantine that player's banking access.
This is conservative detection, not automatic hard-crash repair; no item or money replay is guessed.  The library and
adapter do not promise atomic disk writes across game inventory and world-save blobs.
Player-facing responses use names; `Snapshot`, journal records, and persisted sections are privileged server-only data.
