# DemiBot

Backend helper for the DemiCat Dalamud plugin. Provides a Discord bot and HTTP + WebSocket
API used by the plugin.

## Quickstart

Install dependencies and create the database:

```bash
python -m venv .venv
source .venv/bin/activate
pip install -e .
alembic -c demibot/db/migrations/env.py upgrade head
```

Copy `.env.example` to `.env` and adjust values as needed. The default configuration
uses a local SQLite database and a demo API key.

Environment variables:

| Variable | Description |
| --- | --- |
| `DEMIBOT_DB_URL` | Database connection URL |
| `DEMIBOT_WS_PATH` | WebSocket path (default `/ws`) |
| `DISCORD_TOKEN` | Discord bot token |

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
