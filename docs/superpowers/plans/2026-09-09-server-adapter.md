# Server adapter implementation

Implement the approved `src/server` responsibility boundary around the existing engine-independent banking core.
Preserve the user's responsibility notes and unrelated edits.  Do not add a test suite or deploy a release.

1. Verify the relevant Vintage Story 1.22.7 lifecycle, calendar, inventory, networking, and save APIs locally.
2. Implement validated JSONC configuration, bounded diagnostics, trusted clocks, and guarded world-save storage.
3. Implement authenticated Banker sessions, bounded request dispatch, inventory preflight/rollback, and notices.
4. Wire the adapter into the mod lifecycle and document the integration surface for Banker content and client UI.
5. Build Release and review authorization, corruption handling, item accounting, and documentation conventions.

Exact scan-based liquidity requires explicit user approval because it relaxes the specification's no-account-scan rule.
Until that decision is resolved, do not silently install the scanning implementation as the production default.
The adapter must preserve corrupt save bytes, never recreate established missing authority, and never claim that the
game's independent inventory/world saves are atomically durable.  Unresolved settlements remain quarantined.

Implemented the adapter, native denomination, and integration surface.  Focused review required deferring restore until
`RunGame`, staging during mod disposal before the final engine save, isolating registry quarantine, finalizing malformed
monetary sequences, and checking player-co-serialized receipts against retained bank settlements.
Release compilation passes with zero warnings and errors; no tests or game-world execution were performed.
Full branch content/lifecycle, client UI, printing/Max controls, optional-mod UI/currency adapters, and automatic recovery
remain outside this implemented adapter milestone.  Liquidity scans are an explicit false-by-default opt-in.
