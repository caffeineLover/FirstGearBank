# Banker Charter Implementation Plan

**Goal:** Add the v03 craftable Charter, validated protected premises, and delayed first Banker arrival.

**Architecture:** A custom wall block and block entity expose placement/removal events.  A server-owned Charter lifecycle
uses the native room registry plus bounded semantic furnishing checks, persists exact topology and ordinary-day arrival
progress, denies player structural mutations, and delegates only due Banker homes to `BankerLifecycle`.

**Spec:** `docs/superpowers/specs/2026-09-08-first-gear-bank-v1-design.md`, sections 15 and 17.

**Constraints:** No Harmony, synthetic land claims, core dependency, test suite, or automatic block repair.  Validate
with the installed Vintage Story 1.22.7 sources, strict asset parsing, focused source review, and a Release solution build.

- [x] Add the wall-mounted stateless block, block entity, recipe, model, texture, and localization.
- [x] Add validated room/topology capture, branch spacing, saved placement identity, and arrival timing.
- [x] Add exact-position player protection and removal/decommission integration with the Banker lifecycle.
- [x] Wire startup, save/load, dirty revalidation, documentation, and configuration into the server adapter.
- [x] Verify API and asset references, source conventions, JSON, and the full Release solution build.
