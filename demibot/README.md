# DemiBot

Backend helper for the DemiCat Dalamud plugin. Provides a Discord bot and HTTP + WebSocket
API used by the plugin.

## Quickstart

Install dependencies:

```bash
python -m venv .venv
source .venv/bin/activate
pip install -e .
```

Create the initial configuration file (stored at `~/.config/demibot/config.json`)
by running the service with the `--reconfigure` flag:

```bash
python -m demibot.main --reconfigure
```

You will be prompted for required settings:

* `Server host` (default 127.0.0.1)
* `Server port` (default 5050)
* `Use remote MySQL server? (y/N)`
* `Remote host`, `Remote port` and `Database name` (if remote is selected)
* `Enter Discord bot token`

The answers are written to `~/.config/demibot/config.json`. Edit this file to
adjust `server`, `database` or `discord_token` values (for example, change
`server.port`), or rerun the command with `--reconfigure` to update it.

Run database migrations:

```bash
alembic -c demibot/db/migrations/env.py upgrade head
```
Re-run the migration command after pulling updates to apply any schema changes.

Run the API server (and Discord bot if configured) with:

```bash
python -m demibot.main
```

The Discord bot requires the `GUILD_MESSAGES` and `GUILD_MEMBERS` intents.
After obtaining an API key, the plugin wizard can configure channels via
the `/api/channels` endpoint.

## Notepad API

DemiBot exposes `/api/notepad` for listing sections and pages plus mutation endpoints for creating, renaming, deleting, and reordering content. All write operations require an API key tied to an officer role, while any linked user may read the current state.

Real-time updates stream over `/ws/notepad`; the payloads mirror the REST responses so the Dalamud plugin and any other clients stay synchronized. Each page update carries a `version` field—send it back when saving to perform optimistic concurrency checks. If the stored version is newer, the API returns HTTP 409 so the client can surface a conflict dialog or refresh before overwriting remote changes.

