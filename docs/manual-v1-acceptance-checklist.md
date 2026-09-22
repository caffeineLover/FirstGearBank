# First Gear Bank v1 Manual Acceptance Checklist

This checklist is for the user to run after independently building and deploying the mod. None of these checks were run
during implementation.

## Preparation

- [ ] Make a backup of every save used for banking, registry-recovery, or crash-boundary checks.
- [ ] Use disposable worlds for deliberate shutdown/crash-boundary scenarios.
- [ ] Build and deploy the current working tree yourself; confirm the deployed assets and DLL come from the same build.
- [ ] Keep the server log visible and retain `ModData/FirstGearBank` plus the affected world save after any failure.
- [ ] Confirm `<game data>/ModConfig/firstgearbank.yaml` is created and no JSONC configuration is created or read.
- [ ] With Config Lib installed, confirm all First Gear Bank settings appear and save to the same YAML file.
- [ ] Change one harmless display setting, run `/bankadmin reload`, and confirm the new value applies.

## Existing Charter and Banker behavior

- [ ] Place a Charter in a valid room and confirm the plaque reads “Banker’s Charter” and “First Gear Bank” on two lines.
- [ ] Confirm the plaque is positioned on its wall face and the Banker has a visible face and ordinary idle/look behavior.
- [ ] Confirm the Banker does not remain fixated on the Charter.
- [ ] Look at an active Charter and confirm the HUD says “The bank's charter is active.”
- [ ] Sneak-right-click the Charter as its authorized placer; confirm it is removed and collected without a crash.
- [ ] Re-place and move/remove the Charter repeatedly; confirm no crash and no duplicate branch or Banker.
- [ ] For each invalid-room case, remove exactly one requirement and confirm the HUD names that missing requirement:
  door, enclosure/roof, room size, table/desk, chair/stool, chest/storage, artificial light, potted flower, safe 5×5 floor,
  safe standing space, build permission, single Charter, and branch spacing.
- [ ] Restore every requirement and confirm the same saved arrival wait resumes instead of rerolling.

## Deposits, withdrawals, Max, and retry

- [ ] Deposit and withdraw rusty gears in whole and quarter increments; confirm exact balances and inventory changes.
- [ ] Reject a rusty amount that is not divisible by 0.25 without changing money or inventory.
- [ ] Deposit and withdraw temporal gears only in whole units; reject fractional temporal amounts atomically.
- [ ] Reject a withdrawal above the account balance and a deposit whose complete physical amount is unavailable.
- [ ] Preview a transaction, change the relevant inventory before confirming, and confirm the stale preflight is rejected.
- [ ] With empty inventory space, press Max for rusty and temporal balances; verify the confirmed amount is the largest
  exactly insertable amount and never exceeds the account balance.
- [ ] Repeat Max with partially filled compatible stacks, one empty slot, no empty slots, and a quarter remainder that
  needs its own slot. Confirm no items drop and no partial withdrawal occurs.
- [ ] With no insertable capacity, confirm Max reports an inventory error and creates no financial history entry.
- [ ] Delay after a Max preview, alter balance or inventory, then confirm; verify commit-time checks reject stale facts.
- [ ] Interrupt a financial reply and use Retry; confirm the identical request is resolved once, not charged twice.
- [ ] Close during an uncertain transaction, reopen, and verify history before resubmitting.

## Live and printed statements

- [ ] Confirm the live account, history, totals, rates, CDs, dates, names, and exact balances remain correct.
- [ ] On the account screen, choose Print and confirm the preview shows the holder and frozen balances before approval.
- [ ] Confirm printing consumes exactly one vanilla parchment and creates exactly one nonstacking Bank Statement.
- [ ] Confirm no-account, no-paper, and no-output-space attempts fail without consuming paper or changing bank state.
- [ ] Confirm only one successful statement can be printed in a single Banker conversation, including repeated previews
  and retries of the successful print request.
- [ ] End the conversation and start a new valid Banker conversation; confirm a new one-print allowance is available.
- [ ] Give the statement to another player and confirm the bearer can read its holder, issue day, time basis, exact rusty
  and temporal balances, rates, cumulative totals, CDs, configured recent transactions, and historical-snapshot warning.
- [ ] Change the account after printing and confirm the physical statement remains unchanged.
- [ ] Confirm two printed statements do not stack, including statements with identical visible data.
- [ ] Craft a statement into one Statement Scrap and confirm no account data remains readable on the scrap.
- [ ] Confirm a statement and a scrap can each be used as the configured short-duration fuel.
- [ ] Seal sixteen Statement Scraps in a barrel; after 480 hours confirm the output is exactly one compost.

## Registry recovery administration

- [ ] As an authenticated `controlserver` player, run `/bankadmin registry export`; confirm a unique JSON snapshot appears
  under `<server data>/ModData/FirstGearBank/registry-recovery` and reports a checksum-derived filename.
- [ ] Confirm console-only restore/reset and an online player without `controlserver` are rejected.
- [ ] In a disposable save with a deliberately quarantined registry, export and retain the exact `.bin` evidence.
- [ ] Attempt restore with a missing file, path traversal, malformed JSON, wrong-world snapshot, duplicate/invalid names,
  and binary evidence; confirm each is rejected without altering the quarantined registry or financial accounts.
- [ ] Restore a separately reviewed same-world healthy JSON snapshot with a nonblank reason; confirm old conversations and
  confirmations close, current authenticated players are re-observed, and offline players are not scanned.
- [ ] While only the name registry is quarantined, confirm personal deposits, withdrawals, CDs, and statements remain
  available while recipient lookup/transfers remain unavailable.
- [ ] Run `registry reset reason`; confirm damaged bytes are exported first, a new empty name epoch is installed, current
  connected authenticated players are observed, offline identities are not imported, and all financial records remain.
- [ ] Restart after restore and after reset; confirm the selected registry state and durable recovery audit remain.

## Ambiguous physical settlement recovery

- [ ] In disposable copies, obtain a quarantined rusty deposit, rusty withdrawal, temporal deposit, and temporal
  withdrawal with retained settlement IDs and inventory receipts.
- [ ] For each direction/currency, inspect inventory evidence and run `settlement resolve <id> InventoryApplied reason`;
  confirm only the frozen request-bound financial records publish once and account/inventory agree afterward.
- [ ] Repeat with `InventoryNotApplied`; confirm no deposit/withdrawal record publishes and no inventory is synthesized.
- [ ] Confirm an unknown ID, nonquarantined settlement, invalid finding, blank reason, and repeated resolution are rejected.
- [ ] Confirm resolving one settlement does not duplicate unrelated interest, CD maturity, notification, or correction rows.
- [ ] Confirm the affected player's live quarantine clears only when post-resolution inventory receipts match; otherwise
  access remains held for review.
- [ ] Restart after both findings and confirm the resolution, original manifest/planned records, and audit attribution
  remain durable.

## Natural branches: selection and placement

- [ ] In a new disposable world set probability to `0`, generate several supported vanilla trader structures, and confirm
  none receives a natural branch; reload with `1` and confirm already evaluated sources do not reroll.
- [ ] In another new world set probability to `1`; discover every supported vanilla trader type and confirm exactly one
  natural branch per source, with no branch for modded traders, player-spawned traders, or trader entities alone.
- [ ] Confirm the 7×7 branch lies 20–50 horizontal blocks from the trader structure boundary, fits one chunk column, and
  points its door toward the nearest trader-bound direction.
- [ ] Inspect the complete floor, walls, roof, door, light, table, chair, potted flower, decorative locked strongbox, and
  hidden floor anchor. Confirm there is no Charter and the strongbox has no inventory or loot interaction.
- [ ] Confirm a Banker home is established immediately when safe space is loaded, and ordinary death/replacement and
  return-home behavior match Charter Bankers.
- [ ] Confirm everyone may traverse and use the branch, but ordinary players cannot place/break protected branch blocks.
- [ ] Confirm Charter and natural reservations enforce the configured shared spacing in both placement orders.
- [ ] Provide claims, block entities, liquids, uneven support, player blocks, other generated structures, and insufficient
  clearance at candidate sites; confirm none is overwritten or removed.
- [ ] Confirm unavailable candidate chunks defer the persisted search, later ordinary visits resume it, definite rejected
  candidates are not reconsidered, and an exhausted search remains permanently skipped.
- [ ] Restart and change probability/spacing after decisions; confirm existing rolls, skips, reservations, and placements
  do not reroll or move.

## Existing-world backfill and bounded observation

- [ ] With backfill `false`, visit existing generated vanilla traders and confirm no natural evaluation/placement occurs.
- [ ] Enable backfill and reload; cross into a loaded trader/source or nearby candidate column and confirm evaluation is
  visit-driven. Confirm no startup global scan, offline-player scan, or forced terrain generation appears in logs.
- [ ] Have two players enter the same/overlapping source columns; confirm one source row, roll, reservation, structure,
  claim, home, and Banker result.
- [ ] Disable and re-enable backfill; confirm evaluated sources do not reroll and deferred sources retain their cursor.
- [ ] Visit modded trader structures and player-spawned traders; confirm they remain excluded.

## Restart and deliberate crash boundaries

Use separate world copies for each point and retain logs/save data.

- [ ] Interrupt after durable reservation but before schematic placement; restart and confirm one placement resumes.
- [ ] Interrupt after schematic blocks but before the phase update; restart and confirm the existing anchor/footprint is
  reconciled without duplicating or offsetting the structure.
- [ ] Interrupt after blocks are staged but before anchor identity is bound; restart and confirm the same anchor is bound.
- [ ] Interrupt after anchor binding but before claim creation; restart and confirm exactly one matching claim is created.
- [ ] Interrupt after claim creation but before Banker home establishment; restart and confirm one home and one NPC.
- [ ] Interrupt after home establishment but before final Placed state; restart and confirm no duplicate home, NPC, claim,
  or structure.
- [ ] Introduce duplicate/conflicting anchor or claim evidence in a disposable save; confirm the source is quarantined with
  a `CRIT` log and nothing is automatically deleted or guessed.

## Decommissioning and multiplayer/session isolation

- [ ] Run `/bankbranch decommission reason` while inside exactly one active Charter branch; confirm protection and spacing
  release, conversations close at the documented cutoff, replacement stops, blocks/accounts remain, and restart is clean.
- [ ] Repeat inside a natural branch; confirm its tombstone is staged before cleanup, only its exact mod-owned claim and
  Banker home are removed, the building and all accounts remain, and restart repeats cleanup harmlessly.
- [ ] Confirm blank/oversized reasons, no containing branch, and ambiguous overlap are rejected without changes.
- [ ] Confirm decommission does not delete a player claim or a claim with a mismatched owner/description.
- [ ] Have two players use the same Banker concurrently; verify balances, previews, confirmations, print allowances,
  request sequences, and errors stay isolated per player.
- [ ] Walk beyond six blocks, change dimension, wait beyond the five-minute idle timeout, kill/remove the Banker, and
  decommission its branch; confirm each invalidates only the appropriate sessions.
- [ ] Confirm notices go only to their recipient, acknowledged notices do not normally repeat, and an unacknowledged
  notice can reappear after reconnect without duplicating its financial operation.
- [ ] With several generated traders and players moving through different loaded columns, watch server responsiveness and
  logs for bounded observation, one-time source decisions, and absence of chunk force-loading or duplicate branches.

## Completion record

- [ ] Record the exact build, mod version, Vintage Story version, configuration, world seed, and result for every failure.
- [ ] Preserve the first failing save/log before retrying or modifying it.
- [ ] Treat any crash, duplicate financial operation, item loss/duplication, unauthorized disclosure, overwritten claim,
  forced chunk generation, duplicate branch/NPC, or silent recovery guess as a release blocker.
