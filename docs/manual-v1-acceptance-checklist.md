# First Gear Bank v1 Manual Acceptance Checklist

This is a practical smoke test for a hobby mod release.  Build and deploy the mod yourself, then play normally through
these checks.  There is no need to deliberately corrupt saves, simulate crashes, or test every unusual edge case.

## Quick playthrough

- [ ] Start a world and confirm First Gear Bank loads without an obvious error.  Confirm its YAML configuration file is
  created if you use configuration.
- [ ] Place a Banker's Charter in a valid room.  Confirm it displays “Banker's Charter” and “First Gear Bank,” the HUD
  says the charter is active, and the Banker has a face and behaves normally.
- [ ] Remove one room requirement, such as the chair or flowerpot.  Confirm the HUD explains what is missing; restore it
  and confirm the branch becomes active again.
- [ ] Sneak-right-click the Charter to remove it, then place it again.  Confirm this does not crash the game or create a
  duplicate Banker.
- [ ] Make one ordinary rusty-gear deposit and withdrawal.  Confirm the account balance and your inventory change by the
  expected amount.
- [ ] Print and read one Bank Statement.  Confirm it has the expected account details and does not stack with another
  statement.
- [ ] Restart the world once.  Confirm the Charter, Banker, and account balance remain available afterward.
- [ ] If using natural branches, visit one generated vanilla trader structure.  Confirm any generated bank branch and
  Banker look normal and do not interfere with ordinary play.

## Worth reporting before release

Treat crashes, lost or duplicated items, a missing Charter or Banker after restart, or clearly incorrect balances as
issues worth investigating before sharing the mod.
