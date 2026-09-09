# First Gear Bank

Vintage Story 1.22.7 banking mod under development.  The engine-independent .NET 10 library is in
`First Gear Bank/src/core`; the game adapter is in `First Gear Bank/src/server`.

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
The no-account-scan implementation remains an unresolved design requirement.  Do not pass stale or aggregate-rounded totals.
`ScanningLiquidityIndex` is a ready, exact fallback with linear checkpoint cost.  It is deliberately not selected
automatically; enabling it requires acceptance of that performance exception.

The server adapter implements lifecycle/save staging, comment-preserving JSONC loading, bounded file diagnostics,
trusted calendar/runtime readings, authenticated player observations, Banker conversation validation, bounded network
dispatch, administrator corrections, personal-inventory preflight/rollback, and acknowledged notification delivery.
It waits until `RunGame` to restore financial-clock anchors and stages authority during disposal before the final engine save.

Configuration is `<game data>/ModConfig/firstgearbank.jsonc`.  Existing configuration files are never rewritten by load
or reload.  `AllowExactLiquidityScans` defaults to `false`: banking remains disabled unless an exact index is supplied
through the mod system before startup or an operator explicitly sets this performance-exception option to `true` and
restarts.  No scan fallback has been enabled in a live world by this implementation.

Administrators with `controlserver` can use `/bankadmin status`, `/bankadmin reload`, and
`/bankadmin correct name Rusty|Temporal signedAmount reason`.  Corrections require an authenticated player and an
existing target account.  Trusted server integrations can also call `CorrectBalance` with a related correction ID.
Config Lib remains optional; its custom UI adapter is not implemented, so direct JSONC and the reload command are used.

Banker content must call `Server.RegisterBanker(branchId, entity, availabilityPredicate)` on the server thread after
validating and spawning its branch's Banker, and `UnregisterBanker` on removal.  The predicate must include current
branch availability and damage cutoffs.  Sessions enforce same-dimension six-block reach and a five-minute idle timeout.
Only the authenticated player's own hotbar and backpack participate in banking inventory changes.
The native quarter item and four-quarters-to-one-whole recipe are included; the quarter currently reuses vanilla gear art.
Unverified external quarter integrations are not accepted or selected automatically.

The `firstgearbank-v1` channel registers `BankingPacket`, carrying UTF-8 JSON with PascalCase fields.  Requests contain
`Action`, `Scope`, `Sequence`, and action-specific intent fields; they never carry an account UID.  Supported actions are
`open`, `close`, `statement`, `names`, `confirmTransfer`, `quoteCd`, `deposit`, `withdraw`, `transfer`, `buyCd`, and
`acknowledge`.  Recognized monetary mutations use the core's sequenced retry contract.  Incoming JSON is limited to four
KiB, pages to fifty entries, and queued requests to four per connection and 256 globally.  Client GUI implementation,
physical Max controls, printing, branch topology/protection, charter/replacement spawning, natural-branch generation,
and registry repair administration remain unfinished; this is not yet a playable full-spec release.

Server-only receipts are co-serialized with 1.22.7 personal inventories and checked against successful bank settlement
tombstones on login.  Missing or divergent evidence and pending settlements quarantine that player's banking access.
This is conservative detection, not automatic hard-crash repair; no item or money replay is guessed.  The library and
adapter do not promise atomic disk writes across game inventory and world-save blobs.
Player-facing responses use names; `Snapshot`, journal records, and persisted sections are privileged server-only data.
