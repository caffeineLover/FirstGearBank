# Banking Client Implementation Plan

**Goal:** Implement the approved player-facing banking interface in `First Gear Bank/src/client`.

**Architecture:** A disposable client controller owns one conversation, one outstanding request, and retry state.
A native ledger dialog displays only server-returned views.  Small server additions supply session metadata,
physical-transaction previews, and a watched Banker interaction marker; financial rules remain in core.

**Spec:** `docs/superpowers/specs/2026-09-08-first-gear-bank-v1-design.md`, sections 13.1, 14, and 20.1.

**Constraints:** Vintage Story 1.22.7, .NET 10, no tests or test dependencies, no commits or deployment.
Use the approved `banking-client` branch in the existing checkout; preserve existing changes.
Printing, physical Max, NPC spawning, and branch lifecycle remain separate server/content tasks.

- [x] Add player-safe session metadata and read-only physical confirmation previews in
  `src/server/FirstGearBankServer.cs`; mark only trusted registered Bankers for client interaction.
- [x] Add `src/client/BankingClient.cs` for bounded packet parsing, read correlation, serialized mutation sequences,
  unchanged-payload retries, acknowledged notification display, session cleanup, and right-click interaction.
- [x] Add `src/client/BankingNoticeReceipts.cs` for a bounded persisted display-dedup cache, with opaque world/player
  namespaces and receipt persistence before acknowledgment, as required by spec section 11.3.
- [x] Add `src/client/BankingDialog.cs` and `BankingDisplay.cs` for native account, cash, transfer, CD, history,
  totals, and confirmation pages.  Localized text must distinguish exact amounts from presentation rounding,
  CD informational values from spendable cash, and ambiguous responses from confirmed failures.
- [x] Wire client startup/disposal, add English localization, and update README integration/limitations.
- [x] Release-build the mod and solution; review correlation, double-click/retry handling, token expiry,
  close/reopen behavior, privacy, pagination, comments, and whitespace.  Request focused source review.

Verified local 1.22.7 APIs: `GuiDialog`, `GuiComposerHelpers` text inputs/dropdowns/buttons,
`GuiElementDynamicTextHelper`, `IClientEventAPI.MouseDown/LeaveWorld`, and the existing protobuf channel.
Read replies use negative correlation sequences; mutations alone consume positive core sequences.
Uncertain mutation delivery retains the original packet for explicit retry and never silently issues a new key.
Preview does not reserve funds or inventory; execution rechecks all existing authoritative invariants.

Source review corrections: dispose replaced GUI composers; coalesce queued rebuilds; ignore permanent HUD elements
when detecting world interaction; clear stale snapshots after committed operations; keep mutation snapshot pagination
consistent with the client; notify clients when server scopes close; persist notification display receipts.

Verification: mod and full-solution Release builds succeeded with zero warnings and zero errors.  Focused source
review found no remaining concrete blocker after corrections.  Source comment/whitespace and localization JSON checks
passed for this change.  No tests, commits, deployment, or in-game verification were performed.
