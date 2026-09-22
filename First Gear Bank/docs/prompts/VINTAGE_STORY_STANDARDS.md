# Vintage Story Project Standards
Standards version: 1.0.0
This profile applies to Vintage Story mod projects.

## Prefer Content Mods
- Prefer JSON patches, assets, and other content-mod mechanisms when they express the behavior cleanly and maintainably.
- Use a code mod only when the content system would be unreliable, obscure, or insufficient.

## Mod Configuration
- For config files, prefer YAML over JSON or JSONC.

### Config Lib
- For mods with user-configurable settings, support Config Lib when available but keep it optional.
- The mod must function without Config Lib, using direct configuration-file loading or safe built-in defaults.
- Mods without user-configurable settings need not integrate Config Lib.

## Vintage Story Reference
- The shared `../Vintage Story Reference` contains decompiled assemblies, assets, selected mods, and code-mod sources.
- Use the Reference version matching the target Vintage Story version.  Stop and warn if the version doesn't exist.
- Consult only material relevant to the task and treat the Reference as research, not a build or runtime dependency.
- If the path is unavailable, ask rather than guessing.

## Vintage Story Releases
- Deployment and release packaging are handled manually with CakeBuild.
- When asked, provide ready-to-paste Vintage Story Mod Database changelog text for changes since the previous release.


