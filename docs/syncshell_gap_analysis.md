# SyncShell Feature Gap Analysis

This document summarizes the current DemiCat SyncShell implementation and highlights the
remaining gaps relative to the feature set offered by Mare Synchronos.

## Configuration & Modes

* The plugin configuration still exposes the legacy `SyncshellAutoAllUsers` /
  `SyncshellManualAllUsers` toggles instead of the single radio-style auto/manual mode bit
  requested for the new workflow, and it keeps a plain list of allowed Discord IDs rather
  than a strongly typed allow list keyed by Discord snowflakes.【F:DemiCatPlugin/Config.cs†L151-L179】

## Client UI / UX

* The SyncShell window currently shows status text, basic activity history, and the
  membership roster, but it does not offer mode selection controls, the syncable member
  gallery, invite approval panes, or the aggregate status summary outlined for Mare-like
  parity.【F:DemiCatPlugin/SyncshellWindow.cs†L48-L128】

## Publish Pipeline

* `SyncShellService.PublishAsync` still emits a stub payload (player name only) and never
  collects Glamourer, Penumbra, Customize+, or auxiliary plugin state, so peers cannot
  reconstruct a deterministic appearance bundle.【F:DemiCatPlugin/SyncShell/SyncShellService.cs†L427-L455】

## Client API Surface

* The HTTP client only covers manifest upload, blob transfer, memberships, and presence;
  there are no helpers for `/members/active`, invite management, or targeted publish
  requests. TODO placeholders remain on the blob routes, underscoring the missing
  transport integration.【F:DemiCatPlugin/SyncShell/SyncShellClient.cs†L20-L75】

## Server Endpoints

* The FastAPI layer exposes `/members`, `/memberships`, `/invites`, and `/pending`, but it
  lacks a filtered `/members/active` view or invite accept/decline endpoints under the
  `/invites/{id}` namespace, so the plugin cannot implement the planned auto/manual
  targeting logic yet.【F:demibot/demibot/http/routes/syncshell.py†L680-L834】

## Next Steps

To close the gap with Mare Synchronos, we need to:

1. Rework the configuration schema to add the mutually exclusive auto/manual mode flag and
   persist a typed allow list keyed by Discord IDs.
2. Expand the SyncShell window with mode radios, syncable member panels, invite controls,
   and status counters.
3. Replace the publish stub with real Penumbra/Glamourer/Customize+/extras packaging, blob
   uploads, and targeted manifest delivery.
4. Extend the client API to cover member discovery, invites, and targeted publishes, then
   drive those from the service layer hooks (territory changes, manifest diffs, config
   toggles).
5. Implement the corresponding server endpoints so that filtered member queries, invite
   workflows, and targeted publishes are fully supported.

These changes will bring DemiCat much closer to the Mare experience while preserving the
Discord-centric identity of our deployment.
