---
title: "FIRST GEAR BANK v1"
subtitle: "Codex Implementation Specification"
date: "Specification version 03 · 8 September 2026"
---

# Contents at a Glance {.unnumbered}

| Part | Sections |
|---|---|
| Authority and scope | 1–3: document control, product intent, architecture |
| Banking foundation | 4–7: money, identity, journal, financial time |
| Financial instruments | 8–12: RFR, savings, temporal vault, physical money, transfers, CDs |
| Player experience | 13–14: statements, scraps, Banker NPC, dialogue, GUI |
| Branch system | 15–17: Charter premises/protection, natural branches, recovery |
| Operations | 18–22: config, administration, network, persistence, failure, diagnostics |
| Delivery | 23–26: verification, implementation sequence, acceptance, locked summary |

# 1. Document Control and Design Authority

| Field | Value |
|---|---|
| Product | First Gear Bank of the Old Roads |
| Mod version | v1 |
| Specification | Version 03 |
| Vintage Story target | 1.22.7 |
| Mod ID | `firstgearbank` |
| Status | Design locked; ready for implementation planning |
| Supersedes | Specification version 02 |

This document is the authoritative v1 design. The version 02 document remains preserved as historical source material, but every conflict is resolved in favor of this version.

The words **must**, **must not**, **required**, and **fixed** are normative. Text explicitly labeled as an implementation note may guide structure without prescribing a class name. Concrete Vintage Story types, hooks, asset codes, and third-party integration identifiers must be verified against the installed 1.22.7 reference project before coding; that verification gate does not weaken the required behavior.

No Harmony patch may be added unless public-API source inspection demonstrates a specific unavoidable gap, the proposed patch and risks are documented separately, and the user explicitly approves it. v1 contains no preapproved Harmony patch.

# 2. Product Intent and Scope

First Gear Bank adds a world-level financial institution accessed through Banker NPCs. It lays a durable foundation for later stocks, mutual funds, options, commodities, repos, swaps, short positions, and other instruments without implementing those products in v1.

The v1 playable products are deliberately narrow:

- a fully liquid rusty-gear savings account;
- a temporal-gear vault with stochastic gross interest and an offsetting storage charge;
- fixed-term rusty-gear certificates of deposit (CDs); and
- direct rusty-savings transfers to players successfully registered through observation in the current per-world recipient-registry epoch.

Bankers also accept and dispense physical currency, show live statements, and print immutable paper statements. Banks may enter the world through player-built Charter branches or deterministic natural branches beside some vanilla trader sites.

The economy is intentionally generous. The default long-run risk-free target makes one rusty gear grow to four over one financial year when the short rate remains at its long-run mean. Implementers must not silently rebalance this toward real-world rates.

## 2.1 v1 non-goals

v1 does not include:

- loans, overdrafts, margin, repos, swaps, stocks, funds, options, or commodities;
- joint accounts, inheritance, account merging, or player-selected beneficiaries;
- cross-world or cross-server account migration;
- rusty-to-temporal or temporal-to-rusty conversion;
- temporal CDs;
- CD early redemption, auto-renewal, or a secondary market;
- transfer memos, fees, recipient approval, cancellation, or chargebacks;
- branch reserves or a Banker inventory containing customer money;
- telemetry; or
- compatibility with Useful Drifter Loot.

The general ledger uses signed accounts so future liabilities can be introduced cleanly. That extensibility must not enable a negative savings or temporal-vault balance in v1.

# 3. Architectural Model

Use one mod package organized as a modular monolith. Separate a pure .NET 10 banking core from the Vintage Story adapter.

```text
First Gear Bank mod package
|-- Banking core (.NET 10, engine-independent)
|   |-- fixed-point money and rounding
|   |-- financial clock abstractions
|   |-- CIR process and yield curve
|   |-- savings, temporal vault, and CDs
|   |-- double-entry journal and projections
|   |-- transfers, names, and notifications
|   `-- deterministic state machines and validation
|
`-- Vintage Story 1.22.7 adapter
    |-- ModSystem lifecycle and world persistence
    |-- server networking and authenticated sessions
    |-- inventory and physical-item settlement
    |-- Banker entity, dialogue, and GUI
    |-- Charter blocks, room detection, and protection
    |-- natural-branch world generation and claims
    `-- optional configuration-mod adapters
```

The core must not depend on world blocks, entities, inventories, GUI types, or static engine state. The adapter supplies authenticated player identity, authoritative timestamps, inventory views, room topology, branch access, and persistence envelopes.

## 3.1 Server authority

All financial state is server-authoritative. Clients may request operations and render server snapshots; they never submit a trusted balance, player UID, rate, spread, maturity payoff, inventory result, or final recipient identity.

Every mutation passes through one server-side coordinator. Network handlers enqueue commands rather than editing projections directly.

```text
BankingCoordinator
|-- FinancialClockService
|-- RiskFreeRateService / YieldCurveService / CDSpreadService
|-- AccountProjectionService / CertificateOfDepositService
|-- JournalService
|-- PhysicalSettlementService / DenominationRegistry
|-- TransferService / PlayerNameRegistry / NotificationService
|-- StatementService
|-- PersistenceService / RecoveryService
`-- BranchAccessService
```

The coordinator captures one authoritative timestamp, serializes financial mutations, validates a complete candidate result, and publishes one immutable revision. A revision may contain zero or more ordered, individually balanced journal records plus all matching projection, deduplication, settlement, and notification-outbox changes. A zero-record revision is allowed only for durable nonfinancial control state such as finalizing a rejected authenticated request. The entire batch commits all-or-nothing before a terminal result is reported.

For multi-account commands, participating hidden PlayerUIDs are ordered using ordinal comparison. This deterministic order remains required even while a single coordinator serializes v1, so later concurrency cannot introduce lock inversion or replay-order differences.

## 3.2 World-level institution

An account belongs to a PlayerUID and world save, not to a Banker or branch. A player may deposit through one Banker and withdraw through another. Banker death, entity unload, branch vacancy, player death, player logout, or distance from a branch does not change financial ownership or stop the configured financial clock.

Branches are access points. They hold no reserves. Banker entities never carry customer deposits and never drop ledger-backed funds.

# 4. Monetary Representation and Limits

Use a checked signed 64-bit fixed-point money type.

```text
1 gear        = 1,000,000 bank units
0.25 gear     =   250,000 bank units
0.000001 gear =         1 bank unit
```

All balances, principals, maturity values, interest, storage charges, corrections, and journal postings use this six-decimal scale. Binary floating point may be used only for rates, probability distributions, exponentials, and curve calculations.

Every conversion back to money uses one centralized rule:

```text
RoundToBankUnits(x) = nearest integer bank unit,
                      midpoint ties to even
```

Calculations must reject `NaN`, infinity, integer overflow, and nonrepresentable results before publishing state. No code path may wrap arithmetic silently.

The default player-facing rusty-gear precision is three decimal places and is configurable. Display precision never changes the six-decimal ledger scale. Values shown at fewer than six places are presentation rounding only; statements and confirmations must not imply that a hidden remainder was discarded.

Temporal-vault balances display up to six decimals with trailing zeroes trimmed. This is a presentation rule only; temporal physical settlement remains whole-gear.

## 4.1 Balance ceiling

```text
MaximumBalanceGears = int.MaxValue
MaximumBalanceUnits = 2,147,483,647,000,000
```

Rusty savings and temporal-vault balances are capped at this amount. Player-initiated operations that would exceed a recipient or target balance fail atomically. A CD whose stored maturity value is itself unrepresentable or above the ceiling cannot be issued.

Automatically generated interest saturates the affected cash balance at the ceiling without first constructing an overflowing exponential or posting hypothetical above-cap interest. The resulting accrual record carries nonmonetary `WasCapped` audit metadata; value beyond the hard cutoff is not created, left pending, or retried. Once at the ceiling, an account receives no additional credited interest until its balance falls. An already-contracted finite CD payoff uses the separate representable `CapOverflow` settlement rule in section 12.5. This deliberately treats the ceiling as the user-requested cutoff rather than as a new economic instrument.

Rusty savings and temporal-vault balances must never be negative. Signed journal, expense, income, correction, and future contract accounts may be negative. Future credit exposures must remain distinct contracts and must never silently overdraw savings.

# 5. Identity and Account Lifecycle

One personal banking relationship exists per authoritative Vintage Story `PlayerUID` per world.

- The PlayerUID is the immutable account key.
- The latest canonical player name is display metadata.
- Historical names are retained for audit only.
- PlayerUIDs never appear in player UI, chat, notifications, printed statements, administrator-facing business reports, or normal logs.
- Internal recovery diagnostics may contain a UID only when essential and access-controlled.

An account survives death, logout, player-name changes, and character recreation associated with the same PlayerUID. v1 performs no account merge or cross-world migration.

There is no formal account-opening ceremony. The first successful physical deposit or received transfer creates the account lazily. Players without an account may still speak to a Banker and view rates. Empty accounts are valid after their financial history has begun.

Each account exposes:

- one rusty savings balance;
- one temporal-vault balance;
- zero or more active CDs and immutable matured-CD history;
- accrual checkpoints;
- cumulative deposit, withdrawal, interest, storage, CD-interest, transfer-in, and transfer-out totals; and
- an ordered player-facing view of relevant journal operations.

# 6. Append-Only Double-Entry Journal

The journal is the monetary source of truth. Account balances, CD lists, aggregates, histories, liquidity inputs, notification intents/totals, and other financial read models are projections that must be rebuildable from journal records plus persisted financial-market state. Nonfinancial delivery facts—sealed notification buckets, acknowledgments, delivery watermarks, and physical-settlement evidence—remain separate authoritative control state and are never guessed from money postings.

```text
JournalRecord
+-- Sequence                 // contiguous world sequence
+-- OperationId              // globally unique and immutable
+-- CommandId                // correlates one atomic coordinator batch
+-- RequestKey               // client mutation key, when applicable
+-- SchemaVersion
+-- TransactionType
+-- EffectiveFinancialTime
+-- WorldCalendarTime
+-- Actor/subject keys       // internal identity only
+-- DisplayNameSnapshots     // safe statement rendering
+-- RelatedCdId
+-- StructuredAuditMetadata
`-- Postings[]

Posting
+-- LedgerAccountId
+-- Currency                 // Rusty or Temporal
`-- SignedUnits              // debit positive, credit negative
```

For every journal record and every currency:

```text
sum(SignedUnits) = 0
```

The equality is checked in overflow-safe arithmetic. Rusty and temporal currencies never net against one another. Zero postings are omitted. Sequences, IDs, timestamps, metadata, and postings are immutable. Corrections and reversals are new operations that reference earlier work; there is no update or delete path for committed history.

The displayed balance of a customer liability account is the negation of its cumulative ledger sign. Validation enforces nonnegative customer savings and vault balances.

| Operation | Debit-positive posting | Credit-negative posting |
|---|---|---|
| Physical deposit `A` | physical-custody system account `+A` | customer savings/vault liability `-A` |
| Physical withdrawal `A` | customer savings/vault liability `+A` | physical-custody system account `-A` |
| Savings interest `I` | interest expense `+I` | customer liability `-I` |
| Temporal storage `H` | customer temporal liability `+H` | storage income `-H` |
| CD purchase `P` | rusty savings liability `+P` | CD principal liability `-P` |
| CD maturity `M`, principal `P` | CD liability `+P`; CD-interest expense `+(M-P)` | rusty savings liability `-credited`; `CapOverflow` `-(M-credited)` as defined in section 12.5 |
| Rusty transfer `A` | sender rusty liability `+A` | recipient rusty liability `-A` |
| Admin balance change `D` | system correction account `+D` | target liability `-D` |

`D` is the requested change in the displayed customer balance; a negative correction reverses both signs. `M-P` may also be negative without violating double entry.

One coordinator commit may contain multiple ordered `JournalRecord`s when chronological catch-up crosses months or maturities before the requested business operation. Every record has one transaction type and balances independently per currency; every record in the batch shares a correlation `CommandId`, and the whole batch publishes or fails together. One transfer itself is exactly one journal record. `TransferOut` and `TransferIn` are directional statement views, not separately committable records.

## 6.1 Idempotent requests

Every client mutation uses a `RequestKey` containing the authenticated PlayerUID, a server-issued nonreused operation-scope nonce, and a strictly increasing mutation sequence. A scope accepts one mutation at a time and only its next sequence. The same immutable coordinator revision that finalizes a request advances the persisted high-water mark and records its payload digest, terminal result, operation IDs, and resulting revision. Closed or pre-restart scopes are permanently invalid.

An identical retry whose response remains cached returns that response. Reuse with a different payload is rejected. A sequence at or below the high-water mark after its response body has been pruned returns `AlreadyProcessedResponseExpired` and is never executed again. Successful financial records permanently retain their `RequestKey`; physical settlements retain it in their settlement tombstone. Full response bodies may be bounded. Active scopes retain their high-water state; a closed or pre-restart nonce is invalid because it is absent from the active-scope registry, not because every closed scope is retained forever. Exact committed request keys and settlement tombstones remain permanent replay barriers. Automatic operations use deterministic keys such as `CdMaturity:<CdId>`.

Only successful financial commits enter the journal. Failed, rejected, replayed, or throttled requests create no posting, consume no resource, and start no cooldown, but a terminal authenticated request still advances its mutation sequence so the same failed intent cannot later be replayed under the same key.

# 7. Financial Time

One monotonic world-level financial clock controls:

- risk-free-rate evolution;
- rusty savings and temporal-vault accrual;
- monthly CD level and slope shocks;
- CD issue and maturity timestamps;
- bank-liquidity adjustment decay; and
- activation of changed economic configuration.

Charter arrivals, Banker replacements, and the 17:00 renovation deadline always use ordinary in-game world time instead. Quote expiry, transfer cooldown, and statement burning use real elapsed server time.

One financial year is exactly twelve Vintage Story calendar months. A contract for `T` financial months remains `T` months when `daysPerMonth` changes. Never hardcode a 30-, 360-, or 365-day financial period. Contractual issue and maturity instants are stored on the monotonic financial clock; a world-calendar date is only a basis-appropriate rendering of that financial instant.

## 7.1 `InGame`

`InGame` is the default.

- Financial time follows the Vintage Story calendar.
- Sleeping advances financial time, interest, monthly market states, and CD maturity.
- Player logout does not stop accrual while the world calendar advances.
- Server shutdown does not advance the calendar and accrues no financial time.
- A forward jump processes every crossed event and month boundary in chronological order.
- The financial clock stores cumulative monotonic financial time separately from its raw world-calendar anchor. If the raw calendar decreases, retain cumulative financial time and month serial, discard the negative source delta, and reset only the raw anchor. Later positive deltas append from the retained financial instant. A backward edit never reverses accrual, uncrosses or re-enters a boundary, delays accrual until an old date is regained, or moves a contractual maturity backward.

This is intentionally different from real-life wall-clock banking: an unattended stopped server earns nothing, but in-game sleep advances its world calendar and therefore its `InGame` economy.

## 7.2 `ServerRuntime`

`ServerRuntime` advances only while the server/world simulation is actively running.

- It advances at the same economic speed as ordinary unslept `InGame` progression.
- Sleeping does not accelerate it.
- Player logout does not stop it while the server continues.
- Server shutdown contributes zero elapsed time.
- A paused single-player simulation does not count as active runtime.
- Startup reanchors the monotonic runtime source; no process compares a new monotonic clock with a stale prior-process value.

The adapter must derive the ordinary unslept conversion from verified 1.22.7 calendar speed and API values. It must not assume a fixed number of real seconds per game day when the engine exposes the relevant settings.

## 7.3 Boundary order

Rates and monthly shocks are piecewise constant on half-open intervals:

```text
[monthBoundary[n], monthBoundary[n + 1])
```

When time crosses a boundary:

1. Accrue the closing interval using its stored market state.
2. Settle events effective exactly at the boundary.
3. Activate economic configuration queued for that boundary.
4. Select the new month's short rate under the model-transition rules in section 8.1, performing at most one CIR transition.
5. Draw and persist that month’s global CD level and slope shocks.
6. Recompute the bank-liquidity target checkpoint.
7. Publish the new state for quotes and future accrual.

If several months are crossed, process each in order. A CD that matures partway through a month credits savings at that exact time, after which its proceeds earn savings interest for the remainder.

# 8. Risk-Free Rate

Configuration expresses an effective annual interest return:

```jsonc
// Effective annual interest return as a decimal.
// 3.0 means 300% interest and a one-year growth multiplier of 4.0.
"TargetAnnualizedRiskFreeRate": 3.0
```

Let `q` be that configured value. The internal continuously compounded annual long-run mean is:

$$
\theta = \ln(1+q)
$$

At the default:

```text
q     = 3.0
1 + q = 4.0
theta = ln(4) = 1.3862943611198906...
```

The target means that one gear becomes four after twelve months if the continuously compounded short rate remains at its long-run mean. Under stochastic CIR, it is a calibration target, not a promise that every realized twelve-month path or arithmetic expectation equals exactly four.

The mathematical monthly effective rate at the target is approximately 12.2462%.

For a current continuous short rate `r`, player-facing conversions are:

$$
\text{monthly effective rate} = e^{r/12}-1
$$

$$
\text{annualized effective yield if unchanged} = e^r-1
$$

Do not present `r = 1.386294...` merely as “138.6%” without its continuous-compounding meaning.

## 8.1 Rate models

Support `CIR` and `Constant`; default to `CIR`.

Constant mode uses `r = theta`. CIR uses:

$$
dr_t = \kappa(\theta-r_t)dt + \sigma\sqrt{r_t}\,dW_t
$$

Default parameters:

| Parameter | Default | Meaning |
|---|---:|---|
| `r0` | `theta` | new-world initial continuous short rate |
| `theta` | `ln(1 + TargetAnnualizedRiskFreeRate)` | long-run mean |
| `kappa` | `1.0` | mean-reversion speed per financial year |
| `sigma` | `0.12` | volatility |

`TargetAnnualizedRiskFreeRate` is the sole source of the initial/long-run/constant target. Do not expose conflicting `InitialAnnualRate`, `LongRunAnnualRate`, or constant continuous-rate settings.

For a CIR-to-CIR parameter or target change, derive the new `theta` but preserve the closing realized `r` as the next transition input. CIR then mean-reverts toward the new target; a target change does not reroll or reset the closing rate.

At a financial-month boundary, apply the following transition exactly once after activating the new economic configuration:

| Closing model | Activated model | New-month rate |
|---|---|---|
| `CIR` | `CIR` | Preserve closing `r` as input; draw one exact transition using the activated `theta`, `kappa`, and `sigma` |
| `Constant` | `CIR` | Preserve the closing constant `r` as input; draw one exact transition using the activated CIR parameters |
| `CIR` | `Constant` | Set `r = theta_new`; consume no CIR draw |
| `Constant` | `Constant` | Set `r = theta_new`; consume no CIR draw |

A newly created CIR world starts at `r0 = theta` without an initial transition. Switching back to CIR never resurrects a dormant historical CIR rate. Existing CDs and still-valid locked quotes remain unchanged.

## 8.2 Exact CIR transition

CIR transitions once per financial month with `Delta = 1/12` year. For `kappa > 0` and `sigma > 0`:

$$
e = e^{-\kappa\Delta},\qquad
c = \frac{\sigma^2(1-e)}{4\kappa}
$$

$$
\nu = \frac{4\kappa\theta}{\sigma^2},\qquad
\lambda = \frac{4\kappa e r_n}{\sigma^2(1-e)}
$$

$$
X \sim \chi'^2(\nu,\lambda),\qquad r_{n+1}=cX
$$

Use an exact noncentral-chi-square sampler or a tested Poisson/gamma representation:

```text
N ~ Poisson(lambda / 2)
X ~ Gamma(shape = nu / 2 + N, scale = 2)
```

Do not use Euler simulation followed by a clamp. The mathematical transition keeps rates nonnegative. CIR mode requires finite `kappa > 0` and finite `sigma > 0`; administrators wanting deterministic rates use `Constant` mode. The Feller condition is not required because the exact transition supports a reachable zero boundary.

When `theta = 0`, `nu = 0`. If `r_n = 0`, zero is absorbing and `r_(n+1) = 0`. Otherwise draw the Poisson term normally; when `N = 0`, define `X = 0`, and only when `N > 0` draw `X ~ Gamma(shape = N, scale = 2)`. Never call a gamma sampler with zero shape.

The sampler is lightweight, deterministic for its injected random source, and tested across small and large parameters. Persist each realized monthly rate with its month serial. Reloading never rerolls established history.

# 9. Savings and Temporal Vault Accrual

Within a financial month, the current short rate is constant and compounding is continuous. For balance `B`, rate `r`, and elapsed financial years `DeltaTau`:

$$
B_{end}=B e^{r\Delta\tau}
$$

For a full month the factor is `exp(r/12)`. At `r = ln(4)`, the unrounded mathematical factors are two after six months and four after twelve.

Six-decimal posting can produce bank-unit-scale drift from the unrounded target when monthly entries are rounded. That tiny deterministic rounding effect is accepted; no hidden sub-unit currency exists.

Every automatic balance endpoint uses the semantic helper:

```text
CappedCompoundUnits(U, x) = min(MaximumBalanceUnits,
                                RoundTiesToEven(U * exp(x)))
```

Evaluate it with logarithmic threshold comparisons rather than first forming an overflowing exponential or product. For `U = 0`, return zero. Values below the half-bank-unit rounding threshold become zero; values at the ceiling threshold saturate to the ceiling. The centralized ties-to-even rule remains authoritative at an exact midpoint.

## 9.1 Lazy, backdated posting

Accounts are not scanned every tick. Before a balance mutation, materialize every affected account to the coordinator’s one authoritative timestamp.

- Process every crossed month separately.
- Post nonzero interest with the correct effective timestamp.
- Pro-rate money entering or leaving partway through a month.
- Posted interest becomes principal.
- A zero balance creates no interest entry.
- A read-only view may project without mutation, but must display the same balance immediate materialization would create.
- A global accumulation index may accelerate projection only if effective timestamps and fixed-point results remain identical.

Rate history may be pruned only when every dependent checkpoint has advanced or equivalent exact reconstruction data is retained.

## 9.2 Rusty savings

Rusty savings receives the gross floating risk-free rate and is fully liquid. It has no fee, lock, minimum balance, or withdrawal notice. Physical denomination restrictions affect what can be withdrawn as items, not the internal fractional balance.

Its posted endpoint is `CappedCompoundUnits(B_units, r * DeltaTau)`. If it saturates, credit only the amount from the opening balance to the ceiling, attach `WasCapped`, and create no value for the discarded mathematical excess.

## 9.3 Temporal vault

Temporal gears receive the same gross CIR return as rusty savings plus a separate storage charge whose continuous annual rate is `theta`.

For an opening balance `B` over `DeltaTau` years:

$$
\text{gross}=B e^{r\Delta\tau}
$$

$$
\text{net}=B e^{(r-\theta)\Delta\tau}
$$

$$
\text{storage charge}=\text{gross}-\text{net}
$$

For fixed-point posting:

```text
postedGrossUnits = CappedCompoundUnits(B_units, r * DeltaTau)
postedNetUnits   = CappedCompoundUnits(B_units, (r - theta) * DeltaTau)

grossInterestUnits = postedGrossUnits - B_units
storageChargeUnits = postedGrossUnits - postedNetUnits
```

Gross interest, storage, the customer net change, and cap metadata commit in one atomic balanced accrual batch. `postedGrossUnits` is never installed as an intermediate customer balance. The resulting customer balance is `postedNetUnits`; gross-interest and storage aggregates report representable posted amounts, not hypothetical amounts above the hard cutoff. A `WasCapped` flag records saturation. No above-cap hypothetical accrual is created or retried, while later negative temporal growth may move a capped account below the ceiling.

Below the balance ceiling, Constant mode is exactly flat at each posting because `r = theta`: gross interest is visible and exactly offset by storage. An already capped balance posts neither new gross interest nor storage until it can move below the ceiling. Under stationary CIR, expected long-run log growth is approximately zero because `E[r] = theta`, while the path remains stochastic. This is not literal Brownian motion and it is not necessarily an arithmetic martingale.

The multiplicative rule can decline but cannot cross below zero. Fixed-point rounding may reduce a very small balance to zero. Temporal physical deposits and withdrawals use whole gears; internal accrued fractions remain valid.

# 10. Physical Money and Denominations

Deposits consume physical items and create ledger liabilities. Withdrawals debit ledger liabilities and mint accepted canonical items. The global Bank is not reserve constrained: physical custody accounts provide audit counterparts, while interest remains payable even when liabilities exceed deposited principal.

Accepted denominations are:

| Item | Currency value |
|---|---:|
| Vanilla rusty gear | 1.000000 rusty gear |
| First Gear Bank native quarter | 0.250000 rusty gear |
| Supported Better Loot Gear Part | 0.250000 rusty gear |
| Supported Better Loot Plus Gear Part | 0.250000 rusty gear |
| Vanilla temporal gear | 1.000000 temporal gear |

The native quarter item is always registered and always accepted. Four native quarters craft into one vanilla rusty gear. There is no recipe that cuts a whole gear into quarters.

At startup, verify loaded integrations and choose the quarter dispensed by withdrawals in this order:

1. Better Loot Plus;
2. Better Loot; or
3. the native First Gear Bank quarter.

The current expected external identifiers are mod IDs `betterlootplus` and `betterloot`, with item `betterloot:gearpart`; verify them against the 1.22.7-compatible ecosystem before implementation. Never guess an asset code. If an adapter is unavailable or invalid, fall back safely to the native quarter. Native quarters from an earlier configuration remain depositable when an external provider is active.

Useful Drifter Loot is unsupported.

## 10.1 Atomic inventory settlement

The server re-reads and locks authoritative inventory contents and computes value itself. Every deposit and withdrawal is all-or-nothing.

- A failed deposit consumes no item and posts no value.
- A failed withdrawal posts no debit and delivers no item.
- Explicit requested amounts are never partially filled.
- Inventory capacity and exact denomination delivery are preflighted before commit.
- An exception before publication rolls back staged in-memory inventory changes.

Rusty withdrawals must be multiples of 0.25 gear. Temporal withdrawals must be whole gears. A sub-denomination remainder stays in the account and continues accruing.

`Max` is a separate server-side calculation: it chooses the largest amount that is both available, exactly representable, and fully insertable. It is not permission to partially fulfill another amount.

Example:

```text
Rusty balance: 3.718462
Maximum denomination value: 3.500000
Deliver: 3 whole gears + 2 quarter gears
Remain in savings: 0.218462
```

Pure ledger mutations commit entirely inside one Bank revision. Inventory-bearing commands additionally use a persisted `PhysicalSettlement` state machine with a unique `SettlementId`, `RequestKey`, direction, player, exact input/output manifest, before-state fingerprint, associated operation IDs, and phases `Prepared`, `InventoryApplied`, `BankCommitted`, and `Finalized`. Settlement tombstones are permanent replay barriers even after detailed recovery data is compacted.

The server revalidates under inventory lock, creates the prepared record, stages the exact inventory mutation, builds the matching Bank batch, and publishes success only after the in-memory inventory and Bank revision agree. An exception before publication restores the staged runtime inventory. Startup/login reconciliation resumes a known phase exactly once when authenticated settlement evidence proves which side applied; a pending withdrawal delivery waits without a second debit if capacity is no longer available. Contradictory, tampered, or merely inferred evidence quarantines the affected settlement before further banking access. Never guess from fungible stack totals or silently compensate.

Reconciliation uses this exact matrix:

| Durable evidence after restart | Required action |
|---|---|
| Neither committed Bank batch nor authenticated inventory capsule | Discard an orphan `Prepared` record if present; leave inventory and ledger unchanged |
| Capsule only | Verify its signed canonical manifest/digest and append the predeclared Bank batch once |
| Committed Bank batch only | If the exact before-state fingerprint still matches, apply the predeclared inventory mutation and capsule once; otherwise quarantine |
| Both | Mark `Finalized` without repeating either effect |

Any pending output that no longer fits remains one delivery obligation with its original `SettlementId`; it never creates a second debit. Before the affected player may use banking again, all nonfinal settlements must either reconcile or quarantine.

Vintage Story 1.22.7 does not expose a single durable disk transaction spanning player inventory and the mod world-data blob. During implementation, inspect and crash-test whether a private recovery capsule can be co-serialized with that player's inventory. Such a capsule must carry authenticated immutable canonical data sufficient to reconstruct either side—not only a `SettlementId`. If that mechanism is proven, use it to finish one-sided saves deterministically. If it is not proven, v1's all-or-nothing promise applies to live completed operations and the Bank ledger, while an arbitrary process/power failure inside the cross-blob save window may require settlement quarantine and audited administrator resolution. The mod must state this limitation honestly rather than claim impossible disk atomicity.

Provide a permission-gated settlement-resolution command for a quarantined record. It requires the exact `SettlementId`, a mandatory reason, and one explicit finding: `InventoryApplied` or `InventoryNotApplied`. From that finding and the immutable manifest, the coordinator completes or compensates the missing side in one new audited revision; it never edits old journal records. Audit administrator identity, player name/hidden key, manifests and fingerprints, selected finding, resulting operation IDs, timestamps, and reason. Ambiguous evidence is a human administrative decision, not an automatic heuristic.

# 11. Player Transfers

Transfers move rusty savings from one personal account to another. Temporal balances and CDs cannot be transferred in v1.

The sender must be in a valid conversation with a Banker. The recipient may be offline and need never have used the Bank, but First Gear Bank must have committed that player's observation in the current `RegistryEpochId` for this world. The normal initial epoch begins when the mod is installed. A successful incoming transfer lazily creates the recipient’s account.

There is no fee, memo, recipient acceptance, cancellation, chargeback, or partial transfer. A confirmed transfer is immediate and final.

## 11.1 Name registry and recipient selection

The server maintains a persisted, per-world registry from hidden PlayerUID to the latest authenticated canonical player name. The registry is initialized empty with a new opaque `RegistryEpochId` when First Gear Bank state is first created for that world. This epoch remains fixed across ordinary upgrades, shutdowns, and temporary removal/reinstallation of the mod so long as its world data is retained.

Observation occurs only when, while First Gear Bank is active for this world, the server reports an authenticated player joining or the mod enumerates players who are currently connected and authenticated during startup. Registering a player does not create a Bank account. A player who visited before the current epoch but has not joined during it is ineligible and absent from `KnownPlayerListing`; that player becomes eligible after a later authenticated join only when the registry is healthy and the observation commit succeeds. There is deliberately no historical backfill.

An observation becomes effective only when its insert or canonical-name update commits through the coordinator in an atomic zero-journal-record state revision. The persisted `RecipientRegistryRevision` increments on each effective insert or rename, and the touched entry's `MappingVersion` becomes that revision; observing an unchanged UID/name pair is an idempotent no-op. On rename, append the displaced canonical name to that UID's audit-only history in the same revision. The history survives restart and verified recovery, but its names never resolve or appear in `KnownPlayerListing`. Missing current metadata never deletes an already known identity. Never seed or repair this registry from `IPlayerDataManager.PlayerDataByUid`, another server-wide known-player collection, or data belonging to another world. If the registry cannot be loaded safely, apply the isolated recipient-registry quarantine in section 21.1 rather than broadening eligibility silently.

- Resolution trims input and compares current canonical names case-insensitively.
- Historical aliases never resolve and never appear in the selectable listing.
- Unknown or ambiguous names reject without revealing UIDs.
- A transaction stores the names displayed at commit; later renames do not rewrite history.

The default mode is exact name entry. Small servers may enable a searchable names-only listing.

```jsonc
// RecipientSelectionMode options: "ExactName" or "KnownPlayerListing".
"RecipientSelectionMode": "ExactName"
```

`KnownPlayerListing` omits ambiguous current names and never exposes UIDs, balances, account existence, or online state.

## 11.2 Confirmation and amount rules

Use a two-step server-authoritative confirmation. Resolution returns an opaque short-lived token bound internally to the sender session, `RegistryEpochId`, recipient UID, recipient `MappingVersion`, canonical name, and amount intent. At commit, the epoch, selected entry version, and canonical name must still match, and a fresh case-insensitive resolution of that name must return exactly the same UID. An unrelated player's insert or rename does not invalidate the token; a selected-recipient rename or newly introduced name collision does. The confirmation screen shows the resolved canonical name and either a fixed amount or the explicit `AllAtCommit` rule. An invalidated token requires fresh resolution and confirmation.

A manually entered amount may use at most the configured rusty display precision, default three decimals. Excess decimals are rejected, not silently rounded. Transfer intent is either `ExactAmount` or `AllAtCommit`. For `AllAtCommit`, the token binds the mode rather than a numeric amount; confirmation says "entire balance; exact amount determined at commit" and may show the current balance only as an estimate. Inside the serialized commit, accrue both accounts to one timestamp and transfer the sender's exact six-decimal rusty balance, leaving it exactly zero. A recipient-cap or other failure rejects the whole transfer and never falls back to a partial amount.

At commit, accrue both accounts to exactly the same timestamp, then reject:

- self-transfer;
- zero or negative amount;
- malformed or over-precision input;
- insufficient sender savings;
- a recipient result above the balance ceiling; or
- invalid/replayed session state.

On success, append one balanced sender-debit/recipient-credit operation and publish both projections at one revision. Total Bank liabilities are conserved. Transfers do not change total funding or the liquidity ratio.

## 11.3 Cooldown and notifications

`TransferCooldownSeconds` is configurable, defaults to `1.0`, uses real elapsed server time, and is disabled by exactly `0`.

Only a newly successful transfer starts the sender cooldown. Failed, abandoned, replayed, or throttled operations neither start nor extend it. A deduplicated retry returns the original result.

A committed transfer creates exactly one durable notification intent in the same coordinator revision. Its stable `NoticeId` derives from the transfer operation, recipient, and notice kind. Dispatch occurs after the financial commit and can never roll back or repeat money.

An online recipient is offered an immediate message containing the sender's transaction-time display name and amount. Transfers awaiting an offline recipient accumulate in one persisted summary bucket with the covered operation IDs or sequence interval, count, and exact total. On login the server seals that bucket under one `NoticeId`; later transfers enter a new bucket. Individual sender/amount details remain in history and printed statements.

Retain an outbox entry until acknowledgment and retry after disconnect or restart. Packets carry `NoticeId`; the client suppresses IDs in its bounded persisted display-dedup set, and the server advances a recipient delivery watermark only after acknowledgment. Exactly-once creation and aggregation of intents is required. End-to-end visible delivery is honestly at-least-once with stable-ID duplicate suppression because a client may crash after rendering but before acknowledgment. Notification failure never reverses money.

# 12. Certificates of Deposit

CDs are rusty-gear instruments only. The following are the required defaults; configuration may replace the tenor list with other unique positive whole-month values.

| Tenor | Financial years |
|---:|---:|
| 1 month | 1/12 |
| 3 months | 3/12 |
| 6 months | 6/12 |
| 12 months | 1 |

Default minimum principal is exactly 0.25 rusty gear and is configurable within the validated domain in section 18. Once the active minimum is met, principal may include any six-decimal ledger fraction. A CD is funded only from rusty savings.

There is no early redemption, early withdrawal, secondary market, auto-renewal, or temporal CD.

## 12.1 CIR risk-free curve

In CIR mode, use the standard zero-coupon curve with zero market price of interest-rate risk. The same `kappa`, `theta`, `sigma`, and current `r_t` used for realized evolution price the curve.

For tenor `tau > 0` years:

$$
\gamma=\sqrt{\kappa^2+2\sigma^2}
$$

$$
D(\tau)=(\gamma+\kappa)(e^{\gamma\tau}-1)+2\gamma
$$

$$
B(\tau)=\frac{2(e^{\gamma\tau}-1)}{D(\tau)}
$$

$$
A(\tau)=\left[\frac{2\gamma e^{(\kappa+\gamma)\tau/2}}{D(\tau)}\right]^{2\kappa\theta/\sigma^2}
$$

$$
P(t,t+\tau)=A(\tau)e^{-B(\tau)r_t}
$$

$$
y_{rf}(\tau)=-\frac{\ln P(t,t+\tau)}{\tau}
$$

At `tau = 0`, define `P = 1` without evaluating the quotient. Implement `A` in log space and use stable `expm1`-style calculations where available. Constant mode uses `P = exp(-r tau)` and `y_rf = r` for all maturities. CIR configuration requires `sigma > 0`, so the CIR curve never evaluates its singular zero-volatility form.

All curve yields are continuously compounded annual decimals.

## 12.2 Base and stochastic spread

Let `T` be tenor in months, `b_scale` be configured `BaseSpread.Scale`, and `b_exponent` be configured `BaseSpread.Exponent`.

$$
s_{base}(T)=b_{scale}T^{b_{exponent}}
$$

At the default `b_scale = 0.30` and `b_exponent = 0.395`:

| Default tenor | Default base spread |
|---:|---:|
| 1 month | approximately 0.3000 |
| 3 months | approximately 0.4630 |
| 6 months | approximately 0.6088 |
| 12 months | approximately 0.8006 |

This is an annual continuously compounded spread and is intentionally nonlinear, concave, and upward sloping.

At each financial-month entry, draw one global level shock `L` and one global slope shock `S`, then cache and persist them:

$$
s_{stochastic}(T)=L+S\frac{\ln T}{\ln 12}
$$

At one month the slope contribution is zero; at twelve months it is all of `S`. Do not shock each player, quote, or tenor independently.

The refresh cadence is exactly every financial month and is not configurable in v1. Quotes, account commits, reloads, and configuration refreshes draw no additional shocks. If a prerelease config contains `RefreshMonths`, ignore it with one warning; no value other than the removed value `1` is supported.

Vintage Story 1.22.7 `NatFloat` Gaussian semantics must be preserved and documented. Its `var` is a bounded variation parameter, not statistical variance. The verified form is the average of three uniforms:

```text
Shock(v) = 2v * ((U1 + U2 + U3) / 3 - 1/2)
```

Defaults are `v = 0.05` for level and `v = 0.04` for slope, producing supports `[-0.05,+0.05]` and `[-0.04,+0.04]` respectively.

## 12.3 Global liquidity adjustment

For active CD `i` with original principal `P_i`, define its continuously decreasing remaining financial months at financial instant `t`:

$$
remainingMonths_i(t)=12\max\left(FinancialMaturityInstant_i-t,0\right)
$$

Financial instants in this equation are measured in financial years. Remaining months is a fractional duration, not a rounded calendar label or integer countdown. It follows the selected financial-time basis and the active `daysPerMonth` mapping.

$$
F_t=\sum_i P_i\frac{remainingMonths_i(t)}{12}
$$

Let `D_t` be total economically accrued rusty savings plus original principal of active rusty CDs. Matured-but-lazily-unsettled proceeds count as savings from their contractual maturity instant. Temporal balances are excluded.

$$
u_t=\begin{cases}0,&D_t=0\\F_t/D_t,&D_t>0\end{cases}
$$

Let configured target funding ratio be `u_star`, maximum scarcity boost be `A_plus`, maximum excess-funding reduction be `A_minus`, response width be `w`, and adjustment half-life in months be `h`. Defaults are `u_star = 0.25`, `A_plus = 0.15`, `A_minus = 0.10`, `w = 0.10`, and `h = 1.0`. The target spread adjustment is:

$$
a^*(u)=A_{plus}\tanh\left(\frac{u_{star}-u}{w}\right),\quad u\le u_{star}
$$

$$
a^*(u)=-A_{minus}\tanh\left(\frac{u-u_{star}}{w}\right),\quad u>u_{star}
$$

The observed adjustment approaches the cached target with configured half-life `h`:

$$
a(t+\Delta m)=a^*+[a(t)-a^*]2^{-\Delta m/h}
$$

Liquidity checkpoints occur at financial-month boundaries and after committed operations that change aggregate `F` or `D`, including rusty deposits, withdrawals, CD issue, and CD maturity. At a checkpoint, first decay the observed adjustment to that instant under the previously cached target, then compute `u` and cache the next target. Between checkpoints, the target is held constant and the observed value is evaluated lazily. A mere quote cannot change the state, preventing quote frequency from affecting prices.

The denominator must use economically accrued liabilities, not stale account rows. An aggregate index or equivalent exact projection may be used.

When financial state is first created, assign the current financial-month serial; draw and persist `L` and `S` exactly once for that month; reconstruct `F`, `D`, and `u`; and initialize both observed liquidity adjustment and cached target to `a*(u)`. For a new empty Bank, `u = 0`, so the initial value is `a*(0)`. Persist initialization before serving any quote. Reload restores it without a redraw, and quotes never mutate `L`, `S`, `a`, or their checkpoints.

## 12.4 Final yield and quote

$$
s_{CD}(T,t)=b_{scale}T^{b_{exponent}}+L_t+S_t\frac{\ln T}{\ln12}+a_t
$$

$$
y_{CD}(T,t)=y_{rf}(T/12)+s_{CD}(T,t)
$$

For principal `P0` and `tau = T/12`:

$$
M_{exact}=P_0e^{y_{CD}\tau}
$$

`M_exact` is rounded once to six-decimal fixed-point maturity units using ties-to-even. No unapproved yield or principal floor is applied; the exact payoff is disclosed before confirmation.

A server quote is valid for 30 real-time seconds and is bound to one player, principal, tenor, pricing instant, issuance-rule/config revision, active minimum and tenor eligibility, component snapshot, final yield, and rounded maturity value. It has an opaque unique ID and is single-use/replay-safe. A still-unexpired quote remains valid across a financial-month or configuration boundary because its server snapshot is locked; it is not silently repriced or invalidated by a newly raised minimum or removed tenor.

Confirmation atomically:

1. validates quote identity, Banker session, expiry, single-use state, principal minimum, and tenor against the quote's issuance-rule snapshot rather than current configuration;
2. accrues rusty savings to the confirmation timestamp;
3. requires sufficient post-accrual funds for the exact quoted principal;
4. debits the exact quoted principal;
5. creates a unique immutable CD record with `FinancialIssueInstant = confirmation time` and `FinancialMaturityInstant = FinancialIssueInstant + T/12` financial years;
6. stores the rounded maturity amount and all quote components; and
7. commits the balanced journal operation.

Existing CDs never change when rates, spreads, liquidity, or configuration change.

The contract stores financial instants, not merely world-calendar dates. Under `InGame`, live UI may render the world-calendar date currently corresponding to maturity; that rendering is a projection and may change after a backward raw-calendar reanchor without changing the contract. Under `ServerRuntime`, UI and statements label the basis and show remaining financial months or active-runtime-equivalent duration rather than promising an in-world calendar date. A later time-basis switch cannot move the stored maturity instant. Printed statements freeze the basis label and projection shown when printed.

## 12.5 Maturity

At the exact contractual instant:

1. use the stored maturity units without recalculation;
2. extinguish the active CD liability;
3. credit rusty savings, subject only to the global balance ceiling rule;
4. record principal and CD interest in the journal and aggregates;
5. retain immutable matured-CD history; and
6. let proceeds earn floating savings interest from that instant.

Maturity needs no player action or Banker. Processing may be lazy if its economic result is identical to chronological settlement. Multiple overdue events are processed by effective timestamp, then stable CD ID. A world-level maturity priority queue should make due work low-cost without account scans.

Displayed current accrued value is informational only. First calculate:

$$
\widehat V(t)=\operatorname{RoundToBankUnits}\left(P_0e^{y_{CD}\tau_{elapsed}}\right)
$$

Then define:

$$
V(t)=
\begin{cases}
P_0,&\tau_{elapsed}\le0\\
M,&\tau_{elapsed}\ge\tau_{contract}\\
\operatorname{Clamp}\left(\widehat V(t),\min(P_0,M),\max(P_0,M)\right),&\text{otherwise}
\end{cases}
$$

The two-sided endpoint clamp supports positive- and negative-yield CDs. It is not a principal floor: when `M < P0`, informational value may decline below principal. Issue value is exactly `P0`; maturity value is exactly stored `M`. It is not spendable and creates no interim CD posting.

If adding stored maturity `M` would exceed the savings ceiling, settle only the representable amount:

```text
credited       = min(M, MaximumBalanceUnits - currentSavings)
forfeitedAtCap = M - credited
```

The maturity journal debits CD principal liability by `P`, debits CD-interest expense by `M - P`, credits customer savings liability by `credited`, and credits the `CapOverflow` system account by `forfeitedAtCap`, using the debit-positive/credit-negative convention. This exactly balances, extinguishes the contract, and records the finite cutoff amount. It is never retried.

# 13. Statements, Scraps, Burning, and Compost

## 13.1 Live statement

The live statement is a free server-generated view over the materially accrued account projection and journal index. It shows:

- rusty savings and temporal-vault balances;
- cumulative deposits and withdrawals by currency;
- rusty gross savings interest;
- temporal gross interest and storage charges separately;
- CD interest;
- transfer totals;
- current floating RFR, monthly effective rate, and annualized effective yield;
- every active CD, including principal, basis-appropriate issue/maturity representation, locked yield, current informational value, maturity value, and time remaining; and
- the complete paginated transaction history.

Opening or refreshing the view processes due accrual and maturities at one authoritative timestamp. The client receives display DTOs, never PlayerUIDs or internal ledger-account identifiers.

## 13.2 Printed statement

A player may request a physical statement from a Banker. Printing costs exactly one sheet of paper and no fee. At most one **successful** print is allowed per separate Banker conversation.

A conversation is represented by an opaque server nonce created when the valid Banker interaction opens. It ends on dialog close, disconnect, changing Banker, or Banker unavailability/departure. The nonce carries a `PrintUsed` flag after success; printing need not close the rest of the banking UI. A failed print does not end the conversation, consume paper, or consume the one-print allowance.

Before commit, the server requires an existing account, accrues it, snapshots all content, verifies one paper, and preflights output insertion. Success consumes one paper and produces one non-stackable statement. No account, missing paper, full inventory, stale conversation, or another failure changes nothing.

A printed statement is an immutable bearer-readable snapshot. Anyone holding it may read it, but it grants no account authority. Its versioned attributes contain rendered snapshot data rather than a live account query:

- First Gear Bank of the Old Roads heading;
- account-holder canonical name at print time;
- world-calendar and financial timestamps;
- rusty and temporal balances and aggregates;
- cumulative interest and temporal storage charges;
- current RFR and effective-rate displays;
- all active CDs;
- the configured number of most recent transactions, default ten;
- transaction-time display names for transfers; and
- a clear historical-snapshot notice.

It contains no PlayerUID and never changes after a rename, transfer, accrual, configuration change, or CD maturity.

## 13.3 Scraps, fuel, and compost

One printed statement converts through a one-input shapeless crafting recipe into exactly one generic stackable bank-statement scrap. Conversion irreversibly destroys the personalized snapshot so statements with different data converge on one safe stackable item.

Both intact statements and scraps ignite at 600°C and burn over five real-time seconds. Their fuel value must remain deliberately poor so burning records is flavorful rather than an optimal fuel strategy.

Exactly 16 statement scraps sealed in a barrel for 480 in-game hours produce one compost. There is no reverse paper recipe and no direct intact-statement compost recipe. Implement conversion, combustion, and barrel behavior through verified 1.22.7 data/public APIs without Harmony.

# 14. Banker NPC and Player Experience

The Banker is a custom humanoid NPC representing the First Gear Bank of the Old Roads. It may reuse suitable vanilla humanoid models, animations, navigation, idle behavior, defensive behavior, and sounds through supported APIs/assets.

It is not a repurposed `InventoryTrader`. Right-clicking opens custom Banker dialogue and banking UI, never the ordinary buy/sell cart. Do not patch vanilla trader interaction globally.

An intact staffed branch is available 24 hours per in-game day. The only scheduled closing behavior is the special 17:00 departure after a Charter is removed. A vacant branch has no local service, but all world-level finance continues and the player may use another branch.

Every branch is a public access point. A Charter placer gains no ownership, fee income, queue priority, or account privilege. Any player who can lawfully reach and interact with the Banker may use the branch.

Every branch persists a logical workstation/home anchor and a deterministic safe standing position. For a Charter branch, the mounted Charter is the logical anchor and activation selects a passable interior position associated with it. For a natural branch, the schematic's hidden anchor and spawn marker provide them. Ordinary idle and wandering targets stay inside the branch's walkable interior so the Banker remains findable and usable at all hours; Bankers do not adopt arbitrary nearby beds, traders, workstations, or player structures.

Reactive behavior such as fleeing may temporarily leave that region, after which returning home takes priority over ordinary wandering. If displacement or environmental damage makes the standing position unusable, choose the nearest safe interior position deterministically. If none exists, retain the branch and retry on later safe loads without altering blocks or spawning another Banker. Entity unload remains distinct from death.

After Charter removal, the detached Banker may use the last known home region for movement and service until 17:00, but this grants no protection, claim, topology reservation, or restriction on renovation. If remodeling makes it unsafe, the Banker may stand at the nearest safe reachable position until departure. Use supported 1.22.7 navigation and entity behaviors; this rule does not authorize Harmony.

## 14.1 Voice

Dialogue is localized, formal, and slightly self-important without becoming parody. When English uses early-modern pronouns, it must use `thou`, `thee`, `thy`, and verb agreement consistently. Functional clarity takes priority over flourish, especially for irreversible transfers, CD lockup, missing paper, and failures.

## 14.2 GUI principles

The GUI should feel native to Vintage Story:

- restrained panels;
- ledger/table presentation;
- clear typography and in-world dates;
- gear icons where useful;
- Banker dialogue framing functional screens; and
- pagination/search rather than unbounded rows.

Avoid a modern credit-card aesthetic, neon dashboard graphs, or decorative motion that delays input.

Every irreversible operation has a confirmation step showing server-resolved values. Error messages distinguish insufficient funds, invalid denomination, full inventory, expired quote, unknown recipient, `RecipientServiceUnavailable`, cooldown, and unavailable Banker without leaking internal IDs.

# 15. Banker’s Charter

The Banker’s Charter is a custom wooden wall plaque. Its item is stateless: it carries no branch ID, Banker ID, protected-position list, timer, or financial data.

Its exact shaped recipe is:

```text
F C F
F P F
F G F
```

- `F`: firewood; six pieces consumed.
- `C`: charcoal; consumed.
- `P`: parchment; consumed.
- `G`: one rusty gear; required but not consumed.

Verify the exact 1.22.7 item codes and supported non-consumed-ingredient recipe mechanism before implementation. This requirement does not authorize Harmony.

## 15.1 Premises validation

A Charter may remain placed while invalid, but it creates no branch, protection, timer, or spacing reservation until all requirements pass server-side validation.

One recognized connected room must contain:

1. enclosure, roof, and a door;
2. true interior width from 5 through 13 blocks;
3. true interior length from 5 through 13 blocks;
4. interior height from 2 through 13 blocks;
5. a complete 5×5 interior floor footprint belonging to the same room, with adequate Banker headroom; furniture may occupy positions over that floor;
6. the mounted Charter;
7. a table or desk;
8. a chair or stool;
9. an artificial light source;
10. a chest or other qualifying storage block; and
11. a flowerpot containing an actual flower.

The recognized room must contain exactly one Banker’s Charter. A room containing two or more Charters is invalid, preventing simultaneous activation. Once exactly one remains, it may establish a fresh branch normally through full revalidation; it does not inherit another Charter's branch identity, timer, or Banker.

Dimensions are interior measurements, not outer wall dimensions. Required objects in an adjacent room do not count. An empty pot or non-flower does not satisfy the flower requirement. Natural daylight is not artificial light.

Prefer supported 1.22.7 room/building detection. Any supplemental scan is bounded by the 13-block room dimensions and uses public world/block APIs. Recognize compatible furnishings through semantic classes, attributes, or a small explicit compatibility registry rather than enormous lists of variants.

The Charter placer must possess ordinary `BuildOrBreak` permission for every interior and shell position that will be captured. Charter placement never bypasses an existing land claim.

Invalid plaques enter a low-frequency dirty queue driven by relevant nearby block changes, with a bounded periodic fallback only if needed. Do not scan every Charter every tick.

## 15.2 Branch spacing

`MinimumCharterBranchSpacingBlocks` defaults to `32`. Despite its historical name, it applies to every pairing: Charter-to-Charter, Charter-to-natural, natural-to-Charter, and natural-to-natural.

In the same dimension, measure horizontal Euclidean distance between branch-anchor block centers:

```text
dx = candidateX - existingX
dz = candidateZ - existingZ
distanceSquared = dx * dx + dz * dz
```

For configured radius `R > 0`, reject only when `distanceSquared < R * R`. Perform the products in checked/widened integer arithmetic. The exact boundary is allowed, vertical distance is ignored, and different dimensions do not constrain one another. `R = 0` disables this distance test but never same-room, topology-overlap, natural-source, or one-Banker-per-branch uniqueness.

The following reserve spacing:

- a valid Charter awaiting first arrival;
- a staffed branch;
- an active branch awaiting replacement; and
- a natural branch.

A placement-reserved natural branch also reserves spacing before its schematic is written.

Invalid plaques, decommissioned branches, and detached Bankers waiting to leave at 17:00 do not reserve spacing. Configuration changes affect future establishment only. There is no per-player or global branch count limit.

A new Charter at the same premises after renovation is a successor placement and ignores only its own decommissioned predecessor for spacing; every other reserving branch still counts. Reservations start atomically with accepted Charter activation or natural-site reservation and end on Charter removal or administrative decommissioning.

## 15.3 Activation and topology capture

When a Charter first becomes valid, one atomic server transition:

1. revalidates the room, contents, and placer permission;
2. checks branch spacing, same-premises uniqueness, and protected-topology/claim overlap under the branch-registry lock;
3. allocates a unique branch identity and placement identity;
4. captures and persists protected topology;
5. samples and persists an arrival delay from the active configured range (default 2–5 in-game days); and
6. activates protection and spacing reservation.

Protection begins in the same transition that accepts the premises, before the arrival wait.

Branch spacing does not replace physical uniqueness. Reject any Charter candidate whose captured topology intersects a live or pending Charter capture, a placed natural claim, or a placement-reserved natural footprint, even when spacing is zero. A natural candidate with such an overlap tries its next deterministic site; a Charter remains inactive and reports the conflict. A decommissioned predecessor and its detached Banker do not block a successor after their topology registration has been released.

The captured topology is an immutable exact-position snapshot containing:

- connected interior and reserved-air positions;
- immediate floor, walls, roof, and doors enclosing them;
- the Charter;
- required furnishings, storage, lights, flowerpot, flower/decor host; and
- dependent multiblock parts needed to prevent indirect removal.

It is not an axis-aligned bounding box. Deeper foundation, unrelated upstairs areas, adjacent structures, and other blocks merely inside a bounding box are excluded.

Opening a door does not change the snapshot. If the environment destroys a captured block while the Charter remains, the now-empty position remains protected from player rebuilding until the Charter is removed.

## 15.4 Charter protection

Charter protection is a First Gear Bank deny rule layered on ordinary claim permissions. It does not create a synthetic FGB `LandClaim`.

At captured positions, deny ordinary player:

- placement and breaking;
- chiseling;
- rotation/wrench operations;
- pickup/removal of furnishings or decor; and
- indirect multiblock changes affecting a captured part.

Subject to ordinary claims, allow:

- opening/closing doors;
- opening the qualifying storage and changing its contents;
- sitting in chairs;
- operating lights;
- interacting with the Banker; and
- breaking the Charter itself.

The Charter exception bypasses only FGB structural denial. It never bypasses ordinary `BuildOrBreak` or claim rules. The chest is ordinary player storage, not a reserve or Bank asset.

Preflight every multi-position request. A request that tries to break the Charter and another protected block together is denied; the Charter must first be removed in a completed operation.

## 15.5 Environmental boundary

No environmental immunity is promised. Without a suitable public cancellation hook, FGB does not protect against:

- fire or explosions;
- falling blocks or cave-ins;
- machinery;
- administrative/world-edit actions;
- direct writes by other mods; or
- other non-player environmental mutation.

There is no automatic restoration and no Harmony patch.

If the environment damages another captured block while the Charter survives, protection and an already staffed Banker remain active. A pending first arrival suspends if the premises cease to validate. Ordinary players must break the Charter before repairing protected positions.

If the environment destroys the Charter, process it exactly like a player removal.

## 15.6 Arrival

A valid Charter branch samples one delay uniformly from the active `Charter.ArrivalMinimumDays` through `ArrivalMaximumDays` range, default 2 through 5 ordinary in-game days. Persist both the sampled duration and progress; reload or a later config change never rerolls it.

The timer always uses the world calendar regardless of `InterestTimeBasis`:

- sleep advances it;
- shutdown does not;
- changing the financial clock mode does not affect it.

Before first arrival, invalid premises suspend the exact remaining duration. Revalidation resumes the same timer without duplication. If repair requires Charter removal, that removal cancels the old request; a replacement Charter receives a fresh delay.

Arrival is idempotent. A branch has at most one assigned Banker and one active arrival operation. If the anchor chunk is unloaded when due, mark it due and spawn exactly once on the next safe load rather than force-loading terrain.

## 15.7 Charter removal, renovation, and 17:00

Once Charter removal commits:

- release FGB protection immediately;
- release spacing reservation;
- cancel pending first-arrival or replacement work;
- decommission the old branch identity; and
- leave all accounts and financial state unchanged.

If an assigned Banker exists:

- removal before 17:00 lets that Banker continue full service, including new conversations, until 17:00 that same in-game day;
- removal at or after 17:00 causes immediate departure; and
- sleep or a time jump across 17:00 triggers departure on the first server update after crossing.

At cutoff, stop new requests, close open UIs with an explanation, and reject work that has not entered the indivisible commit section. Only an operation already atomic-committing may finish exactly once. The Banker then despawns without replacement.

Every mutation revalidates its Banker session at execution, so a stale client dialog cannot transact after cutoff.

Replacing the Charter before 17:00 does not cancel or extend the old Banker’s departure. The fresh valid Charter captures remodeled topology immediately, creates a new branch identity, and samples a fresh wait from the then-active arrival range. The detached old Banker is not assigned to it.

## 15.8 Banker death and replacement

Explicit death of a Banker assigned to an active branch creates a vacancy and samples uniformly from the active `BankerReplacement.MinimumDays` through `MaximumDays` range, default 3–7 in-game days. Persisted pending replacement delays never reroll after reload or a later range change.

Death causes no lost money, dropped deposits, fine, reputation penalty, hostility, account freeze, rate change, or CD change. The branch retains protection and spacing while vacant. Replacement processing is idempotent and waits for a safe anchor load when due.

Entity unload is not death. Reconcile persisted assignment and any loaded entity tagged with the branch ID before spawning a replacement.

Do not schedule a replacement for a detached post-Charter-removal Banker, an administratively removed Banker, or a branch decommissioned before its timer completes.

# 16. Natural Branches

Every recognized naturally generated vanilla trader location receives at most one deterministic eligibility roll for a nearby First Gear Bank branch. The roll belongs to the generated trader source, never to a trader entity, respawn, chunk load, player visit, or process run.

Vanilla traders and their structures are never replaced, suppressed, converted, or mutated. Modded and player-spawned traders are not anchors in v1.

## 16.1 Source identity and deterministic roll

Build a versioned stable source ID from verified 1.22.7 generated-structure data, including world/dimension context, canonical vanilla structure code, canonical bounds or anchor, and an FGB identity version/salt.

Do not use runtime entity IDs, process-randomized hashes, or load order. Derive a stable uniform value in `[0,1)` from world seed, source ID, and feature salt. Select when it is below `TraderCompanionProbability`, default `0.15`.

Persist first evaluation and disposition. Restarts, visits, respawns, and later configuration changes cannot reroll it. Useful dispositions include:

- evaluated/not selected;
- selected/awaiting required world data;
- placement reserved;
- branch placed;
- permanently skipped; and
- administratively decommissioned.

Probability changes apply only to unevaluated sources. Source uniqueness and operation IDs prevent duplication independently of branch spacing.

## 16.2 Site selection

Generate a deterministic ordered candidate list. A valid site:

- lies 20–50 horizontal blocks from the vanilla trader structure boundary;
- lies outside the trader claim;
- respects configured branch spacing;
- does not intersect a live/pending Charter topology or a placed/reserved natural footprint;
- does not overlap the trader or another generated structure;
- fits the entire 7×7 exterior footprint in one chunk;
- has safe support and clearance;
- avoids claims, protected areas, incompatible block entities, and player construction;
- permits the entrance to face the trader; and
- requires no extreme terrain bulldozing.

Candidate ordering, rotation, and tie-breaking are deterministic. Four rotations are supported.

Unavailable neighboring world data defers evaluation; it is not a failed site. Once a complete search proves no safe site, store a permanent skip. Never reroll elsewhere, repeatedly retry on visits, destroy terrain, move the trader, or force-load distant chunks.

The configured eligibility probability, default 15%, applies before site failures; realized branches may be fewer.

## 16.3 Natural building and protection

Use one compact universal schematic:

- 7×7 exterior and 5×5 interior;
- one-chunk footprint and four rotations;
- entrance facing the source trader;
- desk/table, chair, flowerpot with flower, and artificial light;
- locked decorative strongbox; and
- hidden internal branch anchor and Banker spawn marker.

The strongbox is unusable and contains no reserves. A natural branch does not use the player-facing Charter and is staffed immediately after safe, persisted placement or on the first safe entity-load cycle.

Create a dedicated protection claim covering the natural structure. It permits public travel and Banker interaction while denying ordinary player placement/breaking. It persists through vacancy. It does not promise environmental immunity or restoration.

Persist placement reservation before mutating the world or spawning the Banker. Startup reconciliation completes safe known stages idempotently. Ambiguous partial placement is quarantined rather than duplicated or overwritten.

## 16.4 Existing-world backfill

`BackfillExistingTraderLocations` defaults to `false`.

When false, no retroactive scan or evaluation occurs, and sources are not marked merely because backfill is disabled. New trader worldgen still receives normal evaluation.

When true, loading or visiting a recognized existing vanilla trader location triggers bounded visit-driven evaluation. Never scan the whole world or force-load distant chunks. Use the same source ID, deterministic roll, candidate order, spacing, and permanent disposition as new generation.

Backfill is stricter about safety. Any claim, player construction, incompatible block entity, uncertain structure identity, ambiguous occupancy, or unsafe terrain rejects a candidate. If no provably safe candidate exists, store a permanent skip. Disabling backfill later removes nothing and erases no outcome.

# 17. Duplicate Prevention and Branch Recovery

Duplicate prevention is absolute.

- At most one natural branch per source ID.
- At most one live/pending branch per Charter placement identity.
- At most one live/pending branch per recognized premises, and no overlapping live/pending protected topology or natural claim, independently of spacing.
- At most one assigned active Banker per branch.
- At most one arrival or replacement operation per branch.
- Activation, placement, arrival, replacement, closure, and recovery are replay-safe.
- Entity reconciliation precedes every spawn.
- No outcome rerolls after reload.

An administratively decommissioned source remains marked so worldgen/backfill cannot recreate it automatically.

## 17.1 Emergency decommission command

Provide a permission-gated command that decommissions the branch containing the administrator’s current position. A mandatory nonblank reason is required.

The command:

1. resolves exactly one branch from topology, claim, or anchor;
2. fails without mutation on none or ambiguity;
3. audits admin display identity, internal identity, branch ID/type, dimension, position, time, and reason;
4. releases Charter protection or natural claim;
5. cancels arrival/replacement work;
6. removes assigned or detached Banker without replacement;
7. marks the branch/source administratively decommissioned;
8. leaves every account, CD, rate, and financial journal record unchanged; and
9. leaves building blocks in place.

An intact Charter at that placement is suppressed from automatic reactivation. Removing and placing a Charter creates a fresh placement identity. A decommissioned natural source stays suppressed unless a future explicit recovery feature is designed.

# 18. Configuration and Optional Integrations

The primary config is commented JSONC. FGB preserves comments when it owns a write. If an external manager strips comments, FGB accepts the valid result and must not rewrite it repeatedly merely to restore prose.

Optional soft integration is provided for Config Lib and Integrated Mod Manager when their verified public APIs are present. Neither is a dependency; all settings remain usable through the normal config file. An unavailable or incompatible integration disables only its adapter and produces one clear warning.

An invalid field logs one precise error and uses its documented default; unrelated valid fields remain in force. Validate against these explicit domains:

| Field | Required domain |
|---|---|
| Enum strings | exact documented closed-enum value |
| `TargetAnnualizedRiskFreeRate` (`q`) | finite `q >= 0` and finite `log1p(q)` |
| `CIR.MeanReversionSpeed` (`kappa`) | finite `kappa > 0` |
| `CIR.Volatility` (`sigma`) | finite `sigma > 0` |
| Persisted/current short rate `r` | finite `r >= 0`; otherwise state is corrupt, not defaulted |
| NatFloat shock distribution/average/variation | verified Gaussian form, average exactly `0`, finite variation `>= 0` |
| `BaseSpread.Scale` | finite and nonnegative |
| `BaseSpread.Exponent` | finite and `0 < exponent <= 1` |
| Liquidity target funding ratio | finite and within `[0,1]` |
| Liquidity maximum adjustments | finite and nonnegative |
| Liquidity response width and half-life | finite and strictly positive |
| Minimum CD principal | positive, six-decimal representable, and no greater than the balance ceiling |
| CD tenors | nonempty, unique positive whole-month integers |
| Probability | finite and within `[0,1]` |
| Display precision | integer within `0..6` |
| Cooldown | finite and nonnegative |
| Spacing | nonnegative integer |
| Arrival/replacement timer range | finite positive values with minimum no greater than maximum |
| Printed recent-transaction count | integer within `0..100` |

Economic configuration also receives cross-field validation before activation. For every configured tenor, evaluate the stable CIR/Constant curve, `log(T)`, base spread, bounded level/slope shock extremes, both liquidity-adjustment bounds, and the maturity payoff for the configured minimum principal at `r = theta` and the current carried rate. All intermediates must be finite and the minimum-principal payoff must be representable under the balance ceiling. If any derived check fails, reject the economic candidate as a unit and retain the last known-good economic configuration; on first load use the complete documented defaults. Do not silently drop individual tenors. Because CIR rates are mathematically unbounded, each live quote still performs principal/current-rate representability checks and rejects an impossible quote without changing state.

## 18.1 Representative v1 config

```jsonc
{
  // InterestTimeBasis options: "InGame" or "ServerRuntime".
  // InGame follows the world calendar, including sleep; shutdown never counts.
  // ServerRuntime advances only while simulation runs; sleep does not accelerate it.
  "InterestTimeBasis": "InGame",

  // RiskFreeRateModel options: "Constant" or "CIR".
  "RiskFreeRateModel": "CIR",

  // Effective annual return. 3.0 means 300% interest: 1 gear -> 4 in one
  // year when the short rate remains at its long-run target.
  "TargetAnnualizedRiskFreeRate": 3.0,

  "CIR": {
    "MeanReversionSpeed": 1.0,
    "Volatility": 0.12
  },

  "Currency": {
    "RustyGearDisplayPrecision": 3
  },

  "CD": {
    "MinimumRustyGearPrincipal": 0.25,
    "TenorsMonths": [1, 3, 6, 12],
    "BaseSpread": {
      "Scale": 0.30,
      "Exponent": 0.395
    },
    "StochasticSpread": {
      "LevelShock": { "dist": "gaussian", "avg": 0.0, "var": 0.05 },
      "SlopeShock": { "dist": "gaussian", "avg": 0.0, "var": 0.04 }
    },
    "BankLiquidity": {
      "TargetFundingRatio": 0.25,
      "MaximumScarcityBoost": 0.15,
      "MaximumExcessFundingReduction": 0.10,
      "AdjustmentHalfLifeMonths": 1.0,
      "ResponseWidth": 0.10
    }
  },

  // RecipientSelectionMode options: "ExactName" or "KnownPlayerListing".
  "RecipientSelectionMode": "ExactName",
  "TransferCooldownSeconds": 1.0,

  "Charter": {
    "ArrivalMinimumDays": 2,
    "ArrivalMaximumDays": 5
  },
  "BankerReplacement": {
    "MinimumDays": 3,
    "MaximumDays": 7
  },
  "MinimumCharterBranchSpacingBlocks": 32,

  "NaturalBranches": {
    "TraderCompanionProbability": 0.15,
    "BackfillExistingTraderLocations": false
  },

  "Statements": {
    "RecentTransactionsOnPrintedStatement": 10
  }
}
```

The long-run/initial/constant continuous rate is always derived from `TargetAnnualizedRiskFreeRate`; there are no parallel contradicting rate fields. The historical key `StochasticCDSpread` must migrate once to canonical `CD.StochasticSpread` if encountered in a development config.

## 18.2 Activation timing

- Economic changes activate at the next financial-month boundary after the old state is accrued to that boundary.
- Existing CDs and printed statements never change.
- Changing `InterestTimeBasis` checkpoints the old clock and begins the new basis without retroactive time.
- Display and recipient-selection changes apply to new conversations.
- Cooldown changes apply to subsequent transfer requests and do not rewrite an active cooldown.
- Statement transaction count applies to later prints.
- Charter-arrival and Banker-replacement range changes apply only to delays sampled afterward; every pending sampled delay remains unchanged.
- Natural probability, spacing, and backfill settings affect only future/unevaluated cases as specified; they never remove existing branches.

# 19. Administrative Financial Corrections

Provide a permission-gated command/API accepting current player name, currency, signed fixed-point amount, and mandatory reason. Resolve the player through the current-name registry used for transfers. The server authenticates the administrator; the request cannot choose admin identity. While that registry is quarantined, name-based corrections reject with `RecipientServiceUnavailable`; there is no UID-entry bypass.

Before correction, accrue the target at the command timestamp. Reject zero, malformed, overflow/cap-breaking, or balance-negative results. Commit one double-entry `AdminCorrection` against a per-currency system correction account.

Audit:

- administrator display-name snapshot and internal identity;
- target display-name snapshot and internal identity;
- currency and exact signed amount;
- world and financial timestamps;
- mandatory reason; and
- operation ID.

Ordinary output and logs show names, never UIDs. Corrections never rewrite balances, CDs, aggregates, or old journal entries directly. Undo by appending an equal-and-opposite correction referencing the original operation. Permission/validation failure creates no monetary record.

An online target receives an immediate clearly labeled correction notice. An offline target receives an aggregated next-login administrative-correction summary, with details retained in history. Correction notices use the same stable `NoticeId`, durable outbox, sealed-summary, acknowledgment, watermark, retry, and visible-delivery caveat defined for transfer notices.

# 20. Networking, Persistence, and Recovery

## 20.1 Network protocol

Register a versioned server channel. Mutation packets express intent only: deposit/withdraw selection, CD principal/tenor, transfer name/amount mode, print request, or opaque confirmation token.

For every Banker operation, the server:

1. authenticates the connection and derives its player;
2. validates the active Banker, distance, branch/session, and conversation;
3. bounds packet size and frequency;
4. rejects client-supplied authority; and
5. forwards a normalized command to the coordinator on the server thread.

Responses echo the request key/sequence and return terminal status, operation IDs when committed, state revision, and a fresh display snapshot. A replay returns its cached response or the explicit expired-response rejection described in section 6.1. Stale confirmations, forged entity/session IDs, and changed-payload sequence reuse reject.

Live history and `KnownPlayerListing` are server-paginated and bounded. Client GUI state is disposable and rebuilt after reconnect. Two players using the same Banker share no account/dialog state.

## 20.2 Persisted envelope

Persist through supported Vintage Story world-save mechanisms:

- schema/migration version and state revision;
- complete append-only journal plus integrity metadata;
- projection checkpoint and account balance/aggregate/history indexes;
- account and CD projections;
- financial clock, month serial, rate history/current CIR state;
- monthly shock and liquidity state/checkpoints;
- independent deterministic RNG domains/versioning;
- recipient-registry epoch/revision, current-name entries and mapping versions, and displaced-name audit history;
- notification outbox, sealed summary buckets, acknowledgments, and delivery watermarks;
- active-scope high-water state, permanent exact committed-key indexes, and bounded cached response bodies;
- active physical-settlement recovery records and permanent compact settlement tombstones;
- branch/source identities, exact protections, dispositions, and timers; and
- diagnostic/config-version metadata needed to interpret state.

Each coordinator commit builds an immutable candidate containing its ordered journal-record batch, all affected projections, settlement/dedup/outbox changes, and revision increment. Validate balance caps, nonnegative customer liabilities, CD invariants, journal sequence/operation uniqueness, request high-water movement, and each record's per-currency zero sums before publishing.

Snapshot one immutable revision for saving and retain the dirty flag until serialization succeeds. The journal remains logically append-only even if the modest v1 envelope is rewritten at autosave.

## 20.3 Schema and startup reconciliation

Schema version 1 includes a migration registry from the outset. A migration reads and validates a complete old envelope, produces a deterministic candidate, and only then replaces live in-memory state. Unknown newer versions reject safely.

Startup reconciliation:

1. reads raw state without placing defaults over it;
2. checks framing/checksum, schema, bounded sizes, sequences, unique IDs, currencies, timestamps, finite market state, and balanced postings;
3. performs registered migrations;
4. replays from a verified checkpoint or sequence one;
5. reconstructs accounts, aggregates, CDs, history, transfer directions, and notification intents while preserving authoritative acknowledgment/watermark state;
6. replaces a bad cache with replay result and logs one diagnostic, never a synthetic money correction;
7. restores clock/rate/liquidity/RNG state and processes due boundaries/maturities once; and
8. reconciles physical settlements, branches, entities, notification outbox, and request indexes before enabling affected mutation or inventory access; and
9. validates the persisted recipient registry, then merges only players currently connected and authenticated in this world before enabling name resolution.

A fresh First Gear Bank state or a registered migration from a schema that predates the recipient registry creates a new epoch with an empty registry before applying the current-connection merge. In a schema version that requires the registry, a missing epoch, revision, entry version, or otherwise invalid registry is corruption, not permission to recreate or backfill it. Startup must never use saved-player or server-wide membership for name reconciliation. A player who joined and left while First Gear Bank was absent remains excluded; a player authenticated and still connected when the mod starts is observed by the current-connection merge.

If the authoritative journal or indispensable financial-market state is corrupt or missing, preserve the raw bytes and enter read-only quarantine. Deny mutations and provide a privileged diagnostic/export path. Never silently reset money, delete CDs, reroll market history, or prefer a stale projection over a broken journal.

Processed-request state survives restart. Retain at least the latest 1,024 full terminal mutation response bodies per player plus every still-relevant quote and settlement detail. Older response bodies may be pruned deterministically. High-water state is retained only for active scopes; exact committed request keys, journal operation IDs, automatic-operation keys, and physical-settlement tombstones remain permanent replay barriers. A closed/pre-restart scope rejects through absence from the active registry, and a pruned committed retry rejects as already processed rather than executing again.

Notification delivery state is not reconstructible from money postings alone. Startup replay may recreate a missing intent deterministically from its transfer operation, but it must preserve sealed-bucket boundaries, acknowledgment, and delivery watermark and must never infer that a displayed message was acknowledged.

## 20.4 Randomness domains

CIR transitions, CD shocks, Charter arrivals, Banker replacements, and natural-source rolls use independent salted deterministic domains. Finance seeds should derive from world seed, financial-month serial, feature salt, and sampler version. Do not use process-randomized `string.GetHashCode` or one mutable stream whose variable draw count shifts another feature.

Persist realized financial states as authority. Deterministic derivation aids testing/recovery but never authorizes rerolling stored history.

# 21. Failure, Security, Privacy, and Performance

## 21.1 Failure behavior

| Failure | Required result |
|---|---|
| Insufficient funds | No partial operation or posting |
| Full inventory | No withdrawal debit, print paper use, or output |
| Missing paper | No statement, cost, or print allowance use |
| Missing compatibility asset | One warning and native-quarter fallback |
| Invalid config | Clear log and documented safe default |
| Arithmetic/time fault | Reject candidate state; preserve prior revision |
| Bad projection cache | Rebuild from journal |
| Corrupt authoritative financial/journal state | Preserve and quarantine; never reset |
| Duplicate request | Return cached terminal result, or `AlreadyProcessedResponseExpired` after response pruning; never execute again |
| Ambiguous cross-blob physical settlement after hard crash | Quarantine affected settlement/access; never guess or silently compensate |
| Notification delivery failure | Preserve stable-ID outbox notice and retry; do not reverse money |
| Recipient-registry corruption | Isolate name-resolution services; preserve the registry bytes; never backfill or widen eligibility |

Recipient-registry quarantine disables exact-name transfer resolution, `KnownPlayerListing`, transfer confirmation/commit, and name-based administrative corrections. Outstanding recipient tokens reject with the distinct `RecipientServiceUnavailable` result. Authenticated-UID access to the player's own deposits, withdrawals, savings, vault, CDs, history, and statements remains available when the journal and those subsystems are healthy.

While quarantined, join events cannot produce a successful registry commit and therefore do not make a player eligible; log a bounded diagnostic rather than building a second untrusted name list. Recovery may either restore a separately verified valid registry snapshot or use a permission-gated audited reset with a mandatory reason.

A restored snapshot must authenticate as belonging to this exact world and contain a valid `RegistryEpochId`, registry revision, exactly one current-name entry per UID, each entry's mapping version, and displaced-name audit history. Duplicate canonical names across UIDs are valid and resolve ambiguously. Install it atomically in a zero-journal-record state revision, invalidate all outstanding recipient tokens even if the epoch matches, merge only currently connected authenticated players, and clear quarantine only after validation and persistence. Never supplement it from historical or server-wide membership.

Reset preserves the corrupt bytes for export, creates a new `RegistryEpochId`, zeroes the registry and its revision, invalidates every outstanding recipient token, and commits as a zero-journal-record state revision. It then observes only players currently connected and authenticated. Anyone else must join again. There is no server-wide or historical recovery backfill. Eligibility always means a successful observation commit in the current epoch; after reset, an observation retained only under the old epoch no longer qualifies.

## 21.2 Privacy and trust boundaries

- Player-facing workflows use names, never UIDs.
- Clients never select account identity or admin identity.
- Printed statements are private only through physical possession and are readable by any holder.
- Known-player listing reveals names only.
- Logs avoid balances and internal IDs unless a permission-gated diagnostic requires them.
- There is no telemetry.

## 21.3 Performance

Normal cost must depend on involved accounts, due events, or nearby dirty topology—not all players or the whole world.

Approved implementation exception (9 September 2026): exact liquidity projections may scan accounts and retained CDs
at financial-month boundaries and funding-changing transactions.  Cache the resulting liquidity target between those
checkpoints; ordinary ticks and quotes must not trigger scans unless due financial events require catch-up.
This exception changes performance only, not balance caps, per-account rounding, interest, or CD-pricing rules.

- Evolve market state at boundaries, not ticks.
- Use lazy account materialization and global indexes/rate segments.
- Index current names and account history.
- Maintain a due-time CD queue rather than polling all accounts.
- Page history/listing responses.
- Maintain notification aggregates incrementally.
- Use indexed protected positions and event-driven Charter dirty queues.
- Make backfill visit-driven; never scan/force-load the world.
- Snapshot immutable state briefly and serialize under ordinary save cadence.
- Retain dirty state after a save error.

Complete journal history is retained in v1 and is expected to remain modest. Stress-test save size and replay time. Future compaction may checkpoint/segment but may not discard authoritative facts.

# 22. Diagnostics and Source-Inspection Gates

Provide permission-gated development commands to inspect:

- current internal CIR rate and derived effective yields;
- CIR zero-coupon curve;
- base, level, slope, and liquidity CD spread components;
- funding ratio and liquidity checkpoints;
- financial-clock basis/position and next events;
- account projection versus journal replay;
- due CDs and processed request IDs;
- Charter validation/captured topology;
- branch/source state and deterministic worldgen disposition; and
- chosen quarter provider and optional config adapters.

Do not spam normal logs every tick.

Before implementation selects concrete types, inspect and document the 1.22.7 reference project for:

- vanilla trader entity and interaction architecture;
- dialogue and GUI extension points;
- generated trader structure identifiers and metadata;
- worldgen and existing-site visit/load hooks;
- room/building APIs and block-change events;
- claim permissions and public interception of player modifications;
- calendar/time-speed and sleep behavior;
- world-data persistence lifecycle;
- player-inventory serialization order and a provably co-serialized private settlement-recovery capsule;
- authenticated player-join and currently-connected-player hooks needed for the current-epoch, per-world recipient registry, explicitly excluding world-independent `IPlayerDataManager` data;
- item fuel/ignition properties and barrel recipes;
- readable document patterns;
- non-consumed crafting ingredients;
- Better Loot/Plus loaded IDs and collectible codes; and
- Config Lib and Integrated Mod Manager public adapter APIs.

Do not reuse names from older 1.21 or early 1.22 builds without verification.

# 23. Verification Matrix

## 23.1 Money, journal, and recovery

- Scale conversion, positive/negative midpoint ties-to-even, overflow, cap, and display independence.
- Every journal record balances independently per currency; correlated multi-record batches commit atomically.
- Journal replay reproduces projections and aggregate totals.
- Deposit/withdraw both currencies, interest/storage, CD purchase/maturity, transfer, correction, inverse correction, and cap overflow.
- Negative customer balance rejects while signed system accounts work.
- Duplicate/reordered packets, changed-payload sequence reuse, response-window eviction, closed-scope replay, and permanent stale-key rejection.
- Multi-record coordinator batches publish all-or-nothing while every journal record balances independently.
- Schema roundtrip, deterministic migration, unknown schema rejection, cache rebuild, and authoritative corruption quarantine.
- Restart/crash injection at coordinator/save phases produces no duplicated Bank-ledger operation or market reroll; cross-blob inventory ambiguity reconciles from proved evidence or quarantines without guessing.
- Permission-gated settlement resolution requires explicit finding/reason, applies one deterministic completion or compensation, and leaves a complete audit trail.

## 23.2 Financial clocks and models

- `q=3` derives `theta=ln(4)` and mathematical target factors.
- `InGame` ordinary progression, sleep, logout, shutdown, forward jumps, and backward reanchor.
- `ServerRuntime` ordinary-speed equivalence, sleep exclusion, pause, logout, shutdown, and restart.
- Nondefault `daysPerMonth`; tenors remain calendar months.
- Exact CIR seeded vectors, nonnegative samples, conditional moments, mean reversion, zero-state, large noncentrality, reload determinism, and RNG independence.
- Invalid CIR parameter domains, `theta = 0` absorbing/point-mass cases, every Constant/CIR model-switch pairing, simultaneous target change, and exactly one-or-zero transition draw as specified.
- Constant and CIR rusty accrual, partial-month mutations, exact chronological lazy catch-up, and no duplicate posting.
- Log-threshold compound evaluation, cap saturation without overflowing intermediates, and no hypothetical above-cap automatic posting.
- Constant temporal gross interest exactly offset by storage; CIR temporal growth above target/decline below; never negative; gross/net cap behavior remains one atomic batch.

## 23.3 CDs and liquidity

- Positive finite CIR discounts, `P(t,t)=1`, short-tenor behavior, and Constant flat curve.
- Default base spread values approximately `0.3000`, `0.4630`, `0.6088`, `0.8006`; configured scale/exponent substitute into every formula.
- Verified bounded NatFloat shocks; one-month zero slope and twelve-month full slope.
- Exactly one global level/slope draw per entered financial month, no quote/reload redraw, and deterministic new-world initialization.
- At default liquidity settings: zero at 25%, positive below, negative above, near `+0.15/-0.10` bounds, and one-month half-life; nondefault parameters substitute consistently.
- Funding rises with issue and declines continuously toward maturity; fractional remaining months and economically accrued denominator.
- All configured positive unique tenors, default 1/3/6/12 list, configured/default 0.25 minimum, locked 30-real-second quote, boundary-crossing quote, single use, and replay rejection.
- A live quote survives a boundary that raises the minimum or removes its tenor because confirmation uses the issuance-rule snapshot.
- Positive- and negative-yield informational value reaches exact principal/maturity endpoints and stays between them.
- `InGame` and `ServerRuntime` maturity representations, backward reanchor, and basis switch preserve the contractual financial instant.
- Existing payoff unaffected by later market/configuration.
- Lazy maturity followed by savings accrual equals on-time settlement.
- No redemption under any Banker/branch state.

## 23.4 Inventory and integrations

- Whole rusty, native quarter, Better Loot/Plus precedence, legacy native acceptance, temporal whole-only, and fractional remainder.
- Four native quarters craft to one vanilla rusty gear; no reverse recipe.
- Full inventory, changed slots, forged item value, exceptions, phased settlement, permanent tombstone, and all-or-nothing live settlement.
- Crash every cross-blob phase with old/new player and Bank snapshots; reconcile only authenticated recovery capsules and otherwise quarantine without guessing.
- Missing external asset falls back without disabling the Bank.

## 23.5 Transfers, identity, and notices

- Empty-registry first install/migration; atomic idempotent join/rename registration; persisted epoch/revisions; eligibility only after successful observation commit in the current epoch; authenticated-current-connection startup merge; exclusion of prior-epoch-only and other-world players; eligibility after a prior-epoch visitor rejoins; persistence across restart/upgrade/reinstallation with retained world data; exact-name/listing modes; case-insensitive current names; aliases not resolving; unknown/ambiguous rejection; ambiguous-list omission; rename race; and no UID exposure.
- Unrelated registry updates leave a confirmation valid; selected-recipient rename or a new same-name collision invalidates it.
- Missing/corrupt required registry isolates name services with `RecipientServiceUnavailable`; authenticated same-world snapshot restoration is atomic and invalidates tokens; audited reset creates a new epoch; neither recovery path backfills, and players joining only during quarantine must rejoin after recovery unless still connected for the recovery merge. Restart/restore retains displaced-name audit history without resolving or listing aliases; a valid restored same-name collision remains ambiguous.
- Offline/no-account recipient creation; self, zero, negative, overprecision, insufficient, cap, and concurrent opposite transfers.
- Manual precision for config values 0–6 and `AllAtCommit` recomputation that drains the exact six-decimal post-accrual balance.
- Same timestamp accrual and total-liability conservation.
- Default one-second and disabled cooldown; only a new success starts it.
- Immediate online notice, sealed offline count/total buckets, stable notice IDs, acknowledgment/watermark recovery, disconnect/restart, duplicate suppression, and detailed directional history.

## 23.6 Statements and scraps

- Full paginated live history and all active CDs.
- One paper/no fee; one successful print per conversation; failures preserve paper/allowance.
- Immutable no-UID bearer snapshot; configured recent count; names frozen at transaction/print time.
- Distinct statements do not stack; conversion yields one common scrap and deletes private snapshot.
- Both items ignite at 600°C and burn five real seconds.
- Exactly 16 sealed scraps plus 480 game hours yields one compost across restart, with no resource-positive loop.

## 23.7 Charter branches

- Exact recipe, including non-consumed rusty gear and retry safety.
- Every premises condition independently missing; exact 5/13 boundaries, 14 rejection, complete 5×5 footprint, adjacent-room objects, flower semantics, and permission denial.
- Spacing inside/at/outside boundary and zero cases across every branch-type pairing, plus multiple-Charter-room and topology-overlap rejection independent of spacing.
- Exact topology capture and exclusion; all denied and allowed actions; ordinary claims remain authoritative.
- Protection persists across restart and starts before arrival wait.
- Environmental damage is not falsely blocked/restored; surviving/destroyed Charter behavior.
- Default and nondefault arrival ranges, sleep, shutdown, suspension/resume, unload, reload, replacement Charter, no duplicates, and no reroll of an already sampled delay after config change.
- Pre-/at-/post-17:00 removal, sleep/time jump, stale UI, atomic cutoff, replacement before departure, and detached Banker death.
- Default and nondefault death-replacement ranges, unloaded entity distinction, timer cancellation, unchanged finance, and no reroll of an already sampled delay after config change.
- Persisted workstation/home anchor, bounded interior roaming, reactive return, displacement recovery, and detached renovation behavior.

## 23.8 Natural branches and backfill

- Vanilla traders/structures/claims unchanged; modded/player traders ignored.
- One stable source roll, same-seed repeatability, load-order/restart independence, probability 0/1, and statistical 15% before placement loss.
- Deterministic 20–50-block candidates, one-chunk 7×7 footprint, four rotations, door facing trader, spacing, terrain/claim/player-work safety, deferral, and permanent skip.
- Crash at every placement checkpoint cannot duplicate or overwrite.
- Immediate staffing, public protected access, locked strongbox, vacancy, and no environmental restoration promise.
- Backfill default false, visit-driven enablement, no global scan, deterministic same source, strict safety, no reroll, and normal-worldgen race.
- Emergency decommission permissions, mandatory audit, stale protection/timer cleanup, no finance change, and no automatic regeneration.

## 23.9 Load and multiplayer

- Many accounts, CDs, years of journal history, names, notices, and request IDs remain within measured save/replay/page budgets.
- No per-tick account/Charter/world scan.
- Multiple players at one Banker retain isolated GUI/account state.
- Multiple Bankers reach the same world account safely.
- Malformed/oversized packets, replay storms, stale revisions, forged sessions, pagination bounds, and disconnects fail safely.
- Every config-domain boundary, cross-field derived economic rejection, first-load default fallback, and last-known-good retention behaves deterministically.

# 24. Recommended Implementation Sequence

This is sequencing guidance, not authorization to begin coding before the implementation plan is reviewed.

1. Inspect the 1.22.7 reference project and record verified APIs/assets.
2. Establish project/build/package metadata for `firstgearbank`.
3. Implement pure fixed-point money, result/error types, and deterministic tests.
4. Implement journal, projections, coordinator, schema envelope, and recovery tests.
5. Implement financial clock abstraction and both time bases.
6. Implement Constant rate, exact CIR sampler, curve, and RNG domains.
7. Implement rusty/temporal lazy accrual and cap behavior.
8. Implement denomination registry, phased physical settlement, reconciliation, and crash-boundary quarantine.
9. Implement CDs, quotes, maturity queue, spreads, and liquidity.
10. Implement the per-world recipient registry and epochs, transfers, cooldown, durable notification outbox, and acknowledgment.
11. Implement networking DTOs and Banker session validation.
12. Implement Banker entity/dialogue and banking GUIs.
13. Implement live/printed statements, scraps, burning, and compost.
14. Implement Charter recipe, validation, topology protection, timers, and renovation state machine.
15. Implement natural schematic, source registry, placement, protection claim, and backfill.
16. Implement configuration adapters, administration, diagnostics, migrations, and localization.
17. Run the full verification matrix, multiplayer/crash/load tests, packaging validation, and rendered player-facing QA.

# 25. v1 Acceptance Criteria

v1 is complete only when a player can:

1. find an unchanged vanilla trader and, for an eligible safe source, a nearby protected natural First Gear Bank;
2. alternatively craft the exact Charter, furnish valid 5×5-or-larger premises, and receive a Banker after the configured delay (default 2–5 in-game days);
3. remodel by breaking the Charter, retain service until 17:00, replace it, and receive a new Banker after the fresh delay;
4. use any Banker to access one world-level personal account by name-facing UI;
5. deposit whole/quarter rusty gears and whole temporal gears without value loss;
6. observe six-decimal-safe continuous savings accrual under the selected no-shutdown clock;
7. observe a stochastic temporal vault with separate gross interest and storage entries;
8. withdraw representable amounts atomically while retaining fractional remainder;
9. buy locked rusty CDs from a 30-second server quote using the default 1/3/6/12-month choices and receive stored maturity proceeds automatically;
10. transfer rusty savings, using current names, to any offline or online player whose observation First Gear Bank has committed in this world's current recipient-registry epoch;
11. view full live history and print one immutable statement per conversation for one paper;
12. convert statements to scraps, burn either item in five real seconds at 600°C, and compost 16 scraps in 480 game hours;
13. kill a Banker without financial loss and receive a replacement after the configured delay (default 3–7 in-game days) at an active branch;
14. restart without duplicated Bank-ledger money, rates, spreads, maturities, branches, notification intents/aggregates, or rerolls; retry notices by stable ID, and reconcile or quarantine a hard-crash inventory settlement without guessing; and
15. use the mod without Config Lib, Integrated Mod Manager, Better Loot, or Better Loot Plus installed.

# 26. Locked Constraints Summary

```text
Target: Vintage Story 1.22.7
Mod ID: firstgearbank
Architecture: one package; pure .NET 10 core + Vintage Story adapter
Authority: server only
Ledger: append-only, double-entry, signed, schema-versioned
Money: 1 gear = 1,000,000 units; ties-to-even; display default 3
Customer balance cap: int.MaxValue gears; savings/vault never negative
Inventory hard-crash boundary: reconcile proved capsule or quarantine; never guess

Financial clock default: InGame
Shutdown accrual: never
InGame sleep: advances finance
ServerRuntime sleep: does not accelerate finance
Year: 12 Vintage Story calendar months

Risk-free model default: CIR
TargetAnnualizedRiskFreeRate: 3.0 effective
theta and r0: ln(4)
kappa: 1.0
sigma: 0.12
Compounding: continuous, pro-rata, lazy/backdated

Rusty savings: fully liquid gross RFR
Temporal vault: gross RFR less theta storage charge; stochastic mean-zero log target
CDs: rusty only; default 1/3/6/12 months and 0.25 minimum; configurable; no early redemption
Quote: 30 real seconds; maturity value stored at issue

Transfers: rusty savings only; current names; successful observation in current epoch
Registry: per-world epoch; starts at install; offline allowed; no historical/server-wide backfill
Recipient mode default: ExactName; optional KnownPlayerListing
Cooldown: 1 real second, configurable; Transfer All uses exact AllAtCommit balance
Notices: durable stable-ID outbox; at-least-once delivery with dedup/ack

Native quarter: always registered; four -> one rusty gear; no reverse
Quarter precedence: Better Loot Plus, Better Loot, native
Useful Drifter Loot: unsupported

Printed statement: one paper, no fee, one successful print per conversation
Snapshot: immutable, bearer-readable, no UID
Statement -> one scrap; both burn at 600C over 5 real seconds
Compost: 16 scraps sealed for 480 in-game hours -> one compost

Charter recipe: F C F / F P F / F G F; gear not consumed
Premises: enclosed 5x5 through 13x13 room, roof, door, headroom,
          table, chair, artificial light, storage, flowerpot + flower
Charter spacing default: 32 horizontal same-dimension blocks; configurable; 0 disables
Protection: exact captured topology against player modification; Charter exception
Environment: no immunity, no restoration, no Harmony
Arrival default: 2-5 in-game days; configured range sampled once
Charter removal: protection ends immediately; Banker leaves at 17:00
Death replacement default: 3-7 in-game days; configured range sampled once; no penalty
Banker home: persisted branch anchor, bounded interior roaming/return

Natural branch chance default: deterministic 15% per vanilla generated trader source
Natural structure: 7x7 exterior, 5x5 interior, 20-50 blocks away, one chunk
BackfillExistingTraderLocations: false by default; visit-driven when enabled
Duplicates: forbidden independently of spacing, including same-room/topology overlap

Optional configuration adapters: Config Lib and Integrated Mod Manager
Administrative money changes: audited double-entry corrections only
Administrative branch recovery: audited decommission at current location
Telemetry: none
Harmony: none unless separately justified and explicitly approved
```

**END OF v1 IMPLEMENTATION SPECIFICATION — VERSION 03**
