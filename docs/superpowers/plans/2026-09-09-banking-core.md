# Banking core implementation

The user authorizes direct implementation of the v03 banking core in `First Gear Bank/src/core`, without a test suite.
This supersedes the earlier foundation plan's test projects, test-first steps, and test-suite completion requirements.
No test suite exists to delete.

1. Implement fixed-point money, financial time, validated economic settings, deterministic CIR, and market history.
2. Implement immutable journal/account state and serialized deposits, withdrawals, accrual, transfers, and corrections.
3. Implement CD pricing/contracts/maturity, registry confirmations, request replay protection, and durable notices.
4. Add checked persistence/replay and host integration contracts; reference the core from the mod project.
5. Build Release and review state publication, rounding, replay, and privacy boundaries.  Do not add tests or packages.

Engine inventory locking, player authentication, Banker access, world-save I/O, UI, and branch behavior belong to the adapter.
The exact-liquidity performance constraint remains a known design boundary; any scan-based exception requires user approval.
