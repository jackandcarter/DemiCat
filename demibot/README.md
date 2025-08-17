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

Run the API server (and Discord bot if configured) with:

```bash
python -m demibot.main
```

The Discord bot requires the `GUILD_MESSAGES` and `GUILD_MEMBERS` intents.
After obtaining an API key, the plugin wizard can configure channels via
the `/api/channels` endpoint.
