# Plugin runtime audit

## Potential runtime conflicts

### Hard reload vs watcher restarts

- `SettingsWindow.HardReloadIdentityAndStartInternalAsync` starts the request watcher, channel watcher, notepad service, chat windows, and templates/event networking in sequence without coordinating with the plugin-level watcher semaphore.【F:DemiCatPlugin/SettingsWindow.cs†L1194-L1355】
- `Plugin.RestartWatchersAsync` uses a separate `_watcherRestartLock` semaphore to stop and start watchers when guild configuration changes, which can race with the hard reload path because the two locks are independent.【F:DemiCatPlugin/Plugin.cs†L904-L1023】
- While each watcher cancels its own token when `Start` is called, overlapping `Stop`/`Start` calls across different services can briefly leave windows in inconsistent states (e.g. chat window networking restarting while the officer watcher is still stopping). Consider funneling every restart through a single coordinator that acquires the same semaphore before invoking `Start`/`Stop` methods.

### Officer chat networking flag
- The `_officerWatcherRunning` flag is only toggled inside `StartWatchersAsync`/`StopWatchers`, so a hard reload triggered from settings can call `OfficerChatWindow.StartNetworking()` without updating the flag. A subsequent `RestartWatchersAsync` will then skip restarting officer networking because it believes it is already running.【F:DemiCatPlugin/Plugin.cs†L1049-L1087】
- Move the flag management into `OfficerChatWindow.StartNetworking()`/`StopNetworking()` or gate officer networking behind the same semaphore used for the other watchers to keep state consistent.

### Presence lifecycle
- Presence readiness is toggled in multiple places (`ChatWindow.StartNetworking`, `ChatWindow.StopNetworking`, `SettingsWindow.HardReloadIdentityAndStartInternalAsync`, and `StopWatchers`), which risks out-of-order updates if two code paths run concurrently.【F:DemiCatPlugin/ChatWindow.cs†L656-L676】【F:DemiCatPlugin/SettingsWindow.cs†L1194-L1329】【F:DemiCatPlugin/Plugin.cs†L1094-L1124】
- Consolidate presence resets inside a single helper that runs on the framework thread to avoid interleaving updates from competing restart flows.

## Alignment with Dalamud platform APIs
- Dalamud v9 removed `IDalamudPluginInterface.CreateHttpClient()`, so the plugin now constructs a singleton `HttpClient` with a `SocketsHttpHandler` that opts into `HappyEyeballsCallback.ConnectAsync` for dual-stack connection attempts while keeping decompression, pooling, and timeout behavior consistent.【F:DemiCatPlugin/Plugin.cs†L115-L177】
- Emoji font integration currently reflects into `Dalamud.Interface.ManagedFontAtlas`. Dalamud 9 introduced helper extensions through `IManagedFontAtlas`. Investigate whether those cover the current reflection usage to simplify upgrades when the managed atlas API changes.【F:DemiCatPlugin/Plugin.cs†L181-L352】


## Tooling to stay on .NET 9.0.3 and Dalamud 13
- `global.json` already pins the .NET SDK to `9.0.300`, matching Dalamud 9.0.3 expectations.【F:global.json†L1-L6】
- Added `scripts/verify-sdk.sh` to surface the active SDK, ensure `9.0.300` is installed, and perform a scoped restore for the plugin project. Running this script after SDK updates catches drift early.
- Future improvements could add CI automation that runs `dotnet format` and the Dalamud packager in `--dry-run` mode to confirm compatibility before distributing builds.
