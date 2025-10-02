# DemiCat

DemiCat pairs a rich Dalamud plugin with the DemiBot backend so Final Fantasy XIV free companies can plan events, mirror Discord conversations, share in game requests, and synchronize appearance mods with Syncshell.

## Quick links

- [Build & Run instructions](docs/BUILD-RUN.md)
- DemiBot quickstart (demibot/README.md)

## Component overview

### Dalamud plugin (`DemiCatPlugin/`)

The plugin hosts a dockable hub window (`/mew`) that stitches together FC chat mirroring, officer communication, event tooling, request tracking, a collaborative note pad, and Syncshell controls. It talks to DemiBot over REST and WebSocket APIs, keeping UI panes live while respecting officer permissions.

### DemiBot service (`demibot/`)

DemiBot is a FastAPI + Discord.py worker that stores guild state in MySQL. It exposes the REST/WebSocket contract that the plugin consumes, runs background jobs (recurring events, channel-name refresh, asset pruning, Syncshell cleanup), and supplies slash-command setup wizards for officers.

### Shared UI (`DemiCat.UI/`)

Reusable ImGui widgets—button rows, markdown helpers, date-time pickers, and Discord validation utilities—are shared between the event and template editors for consistent styling.

## Plugin experience

### Dock & navigation

The main dock aggregates feature tabs—Events, Create Event, Templates, Request Board, NotePad, FC Chat, Officer Chat, and Syncshell—and remembers layout, auto-open preferences, and officer gating. Toggle the dock with `/mew` or through Dalamud’s plugin list.

### Discord chat bridge

FC and officer chat panes stream Discord messages in real time, complete with embeds, emoji reactions, slash-command buttons, typing indicators, attachment previews, and inline embed styling controls. A flexible composer supports Markdown shortcuts, mention suggestions, file uploads, and optimistic send/edit workflows that keep the Discord bridge in sync.

### Event planning workflow

The Create Event window lets users pick channels, schedule start times with a picker, format descriptions with Markdown + emoji, attach artwork, configure banner colors, and define RSVP buttons. Mentionable roles come from the backend, presets can be saved/loaded, repeats generate schedules, and previews render the final embed before posting.

### Templates & presets

Template management fetches event templates over WebSocket, lists them with live channel selection, and lets users preview, post, delete, or tweak button rows and mention roles without leaving the plugin. Time overrides and chat-bridge updates keep template broadcasts synchronized with Discord.

### Request board

The request board is a member help area for those looking to hire out work, posts sort by type/name/recency, and offers an inline modal to add new requests with type and urgency. Status updates propagate via the backend so legacy approval fields stay visible during migration.

### Collaborative note pad

NotePad organizes guild knowledge into colored sections and pages. Players can reorder sections/pages with drag-and-drop, rename items inline, resize the page list, and edit content with autosave. Conflict dialogs appear when server versions diverge, and keyboard shortcuts accelerate common actions.

### Syncshell mod management

Syncshell integrates Penumbra to sync appearance mods: choose auto/manual sync modes, trigger manual pushes, adjust transfer limits, resolve Penumbra conflicts, and send invites via a suggestion-driven picker. Member permissions, invite history, and Syncshell state updates stream in, while a progress overlay shows transfer progress in real time.

### Settings panel

The settings window handles API linking, feature toggles (FC chat, Syncshell, overlays), Penumbra directory overrides, cached-data cleanup, sync initiation, and Discord role refresh. Appearance options adjust chat font/imagery, theme colors, dock behavior, opacity, and fade-out timers with contextual tooltips and previews.

## DemiBot capabilities

### FastAPI + WebSockets

DemiBot auto-discovers route modules, wires REST endpoints, and hosts WebSockets for messages, embeds, templates, officer chat, presences, channel updates, requests, note pad data, chat streams, and Syncshell updates. A startup hook preloads Syncshell transfer budgets, and request logging middleware surfaces API health.

### Background tasks

Recurring jobs keep data fresh: re-post recurring events, resync channel names via Discord REST/gateway, purge soft-deleted assets, and prune expired Syncshell pairings/rate limits.

### Discord bot & setup wizard

The Discord bot loads every cog/module, syncs global and dev-guild commands, and exposes `/demi status`. Officers run `/demi setup` or `/demi settings` to launch a five-step wizard that collects event/FC/officer channels plus officer and mention roles, validates selections, and writes guild config while cleaning stale records.

### Configuration & data storage

On first run `python -m demibot.main --reconfigure` writes `~/.config/demibot/config.json` with server binding, MySQL profiles, and the Discord token. Required credentials are prompted until validated, and the backend initializes its async SQLAlchemy engine before serving.

### Plugin integration

When the plugin links an API key it spins up channel and request watchers that subscribe to DemiBot WebSockets, refresh data after reconnects, and surface permission issues. The presence service tracks Discord member availability, and PingService safeguards API reachability so every UI pane stays responsive.

## Support & contributions

- Rerun the Setup wizard whenever Discord roles or channels change.
- Apply new Alembic migrations after pulling backend updates.
- File issues or PRs with reproduction steps for UI quirks, API drift, or Syncshell edge cases.
