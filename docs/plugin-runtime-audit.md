# Plugin runtime audit

## Potential runtime conflicts

### Hard reload vs watcher restarts
- `Plugin.WithWatchLock` now provides a single coordination point for every watcher restart, and `SettingsWindow.HardReloadIdentityAndStartInternalAsync` executes entirely within that scope so hard reloads and guild updates no longer race each other.【F:DemiCatPlugin/Plugin.cs†L914-L930】【F:DemiCatPlugin/SettingsWindow.cs†L1210-L1333】
- Follow-up: continue to audit individual watcher implementations to ensure they respect cancellation tokens while the lock is held so long-running network calls cannot stall unrelated restarts.

### Officer chat networking state
- Officer networking state is tracked inside `OfficerChatWindow` through an internal cancellation token so the plugin no longer maintains a duplicate `_officerWatcherRunning` flag. All officer start/stop calls now flow through the shared watch coordinator.【F:DemiCatPlugin/OfficerChatWindow.cs†L37-L91】【F:DemiCatPlugin/Plugin.cs†L1087-L1135】

### Presence lifecycle
- Presence readiness toggles have been centralized into `Plugin.SetPresenceAsync`, which marshals the updates onto the framework thread and is only invoked from within watch-locked flows.【F:DemiCatPlugin/Plugin.cs†L468-L487】【F:DemiCatPlugin/SettingsWindow.cs†L1210-L1333】
- Individual windows no longer call `SetPresenceReady` during their start/stop routines, reducing the risk of out-of-order updates when multiple restart paths overlap.【F:DemiCatPlugin/ChatWindow.cs†L658-L676】【F:DemiCatPlugin/OfficerChatWindow.cs†L41-L91】

## Alignment with Dalamud platform APIs
- `Plugin.Initialize` now calls `IDalamudPluginInterface.CreateHttpClient()`, so proxy, retry, and telemetry behavior stay aligned with Dalamud without custom handler maintenance.【F:DemiCatPlugin/Plugin.cs†L115-L162】
- Emoji font initialization first attempts to use the modern `AddFontFromFile` API exposed by the managed atlas facade and gracefully falls back to the legacy reflection pipeline when the interface is unavailable.【F:DemiCatPlugin/Plugin.cs†L520-L613】

## Tooling to stay on .NET 9.0.3 and Dalamud 13
- `global.json` already pins the .NET SDK to `9.0.300`, matching Dalamud 9.0.3 expectations.【F:global.json†L1-L6】
- Added `scripts/verify-sdk.sh` to surface the active SDK, ensure `9.0.300` is installed, and perform a scoped restore for the plugin project. Running this script after SDK updates catches drift early.
- Future improvements could add CI automation that runs `dotnet format` and the Dalamud packager in `--dry-run` mode to confirm compatibility before distributing builds.
