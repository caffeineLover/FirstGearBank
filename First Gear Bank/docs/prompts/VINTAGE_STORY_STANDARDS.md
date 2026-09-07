# Vintage Story Project Standards

Standards version: 0.4.0

This profile applies to Vintage Story mod projects.

## Prefer Content Mods

Prefer JSON patches, assets, and other content-mod mechanisms when they express the required behavior cleanly and
maintainably.  Use a code mod only when the content system would be unreliable, obscure, or insufficient.

## Config Lib

For mods with user-configurable settings, support Config Lib when available but keep it optional.  The mod must function
without it, using direct configuration-file loading or safe built-in defaults.  Its absence must not disable unrelated
features or cause avoidable errors.  Mods without user-configurable settings need not integrate Config Lib.

## Vintage Story Reference Project

The shared **Vintage Story Reference** contains decompiled game assemblies, assets, selected mods, and decompiled
code-mod sources.  Resolve it from the mod repository root at:

`../Vintage Story Reference`

If the path is unavailable, ask the user rather than searching broadly or guessing.  Use the Reference version matching
the mod's target game version.  Consult its index first when practical, retrieve only material needed for the task, and
do not analyze the entire Reference from an ordinary mod project.  Treat it as research, not an undocumented build or
runtime dependency.

## Vintage Story Releases

Provide ready-to-paste changelog text for the Vintage Story Mod Database.  Derive it from the release-history entry and
normally omit version, tag, and release-date metadata.

After producing and verifying the release ZIP, deploy it to this path relative to the current user's roaming
application-data directory (`%APPDATA%` on Windows):

`StoryForge\installations\working_test_world\Mods`

Verify the deployed ZIP matches the source and read its mod ID from `modinfo.json`.  Inspect other ZIPs in the
installation directory and delete only those whose `modinfo.json` mod ID exactly matches the deployed mod's ID.  Never
infer identity from filenames or delete the new artifact or a ZIP whose mod ID cannot be confirmed.  If matching is
ambiguous, ask before deleting.

Open File Explorer to the installation directory so the user can inspect the deployed package.
