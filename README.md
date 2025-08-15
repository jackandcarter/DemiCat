# DemiCat Monorepo

DemiCat connects Final Fantasy XIV with Discord by embedding Apollo event posts directly into the game.

```
DemiCat/
├── demibot/         # Python Discord bot and REST interface
└── DemiCatPlugin/   # Dalamud plugin that renders the embeds in FFXIV
```

## Prerequisites

- [Python 3.10+](https://www.python.org/)
- A database (SQLite by default, MySQL optional)
- A Discord bot token and Apollo-managed channels
- FFXIV with the [Dalamud](https://github.com/goatcorp/Dalamud) plugin framework

## Environment Variables

Copy `demibot/.env.example` to `demibot/.env` and adjust values as needed.
Key environment variables include:

- `DEMIBOT_DB_URL` – Database connection URL
- `DEMIBOT_WS_PATH` – WebSocket path (default `/ws/embeds`)
- `DISCORD_TOKEN` – Discord bot token

## Setup

### 1. Install dependencies and initialize the database
```bash
cd demibot
python -m venv .venv
source .venv/bin/activate
pip install -e .
alembic -c demibot/db/migrations/env.py upgrade head
```
Re-run the migration command after pulling updates to apply any schema changes.

### 2. Configure and start the bot
```bash
cp .env.example .env
python -m demibot.main
```
The first run will start the Discord bot and HTTP API using the values in `.env`.

With the bot online, run `/demibot_embed` in your Discord server to receive a DM with
your **Key** and **Sync Key**. Each user should generate their own keys with this
command for plugin authentication.

### 3. Configure the Dalamud plugin
Update `DemiCatPlugin/DemiCatPlugin.json` with the usual plugin metadata. In-game, open the plugin configuration and set the
**Helper Base URL** if needed (defaults to `http://localhost:8000`).

Use the **Key** and **Sync Key** obtained from `/demibot_embed` and enter both values in the
plugin settings. Press **Connect/Sync** (or **Validate** if you already have a key) to link the
plugin with the bot.

### 4. Insert API keys
Insert API keys into the `api_keys` table to authorize HTTP requests:

```sql
INSERT INTO api_keys (api_key, user_id, is_admin) VALUES ('your-api-key', 'discord-user-id', 1);
```
Use the value from `api_key` as the `X-Api-Key` header when calling the REST API.

## Building and Running

### Dalamud plugin
```bash
cd DemiCatPlugin
dotnet build
```
The build output `DemiCatPlugin.dll` can be found under `bin/Debug/net9.0/`. Copy it into your Dalamud plugins folder and enable it.

Alternatively, add this repository to Dalamud so it can install and update the plugin automatically:

1. In-game, open **Dalamud Settings → Experimental → Custom Repositories**.
2. Add `https://github.com/jackandcarter/DemiCat/raw/main/repo.json` (GitHub redirects automatically). Do **not** use `blob` links or the repository root.
3. Enable the **DemiCat** plugin from the available plugin list.

The plugin icon is hosted at `https://cdn.discordapp.com/attachments/1337791050294755380/1337854067560550422/Demi_Bot_Logo.png`.
Update `IconUrl` in `DemiCatPlugin/DemiCatPlugin.json` and the matching entry in `repo.json` if this image changes.
When releasing, bump `AssemblyVersion` and `FileVersion` in `DemiCatPlugin/DemiCatPlugin.csproj`,
and keep `DemiCatPlugin/DemiCatPlugin.json` and `repo.json` in sync with the new version number.

## Usage

With the bot running and the plugin enabled, open the in-game **Events** window to view live Apollo embeds. Players can click the RSVP buttons to respond directly from FFXIV.

## Discord Commands

The bot registers several slash commands for server administrators and users. Run `/demibot_setup` during initial installation;
use `/demibot_settings` to adjust configuration later. `/demibot_reset` restarts the setup wizard, while `/demibot_clear` removes
all data for the guild.

- `/demibot_setup` – start the interactive wizard to select event, Free Company, and officer channels and choose officer roles.
- `/demibot_settings` – reopen the setup wizard with current settings for further adjustments (administrator only).
- `/demibot_resync [users]` – resync stored role data. Supply space-separated user mentions or IDs to resync specific members or
  omit to resync everyone (administrator only).
- `/demibot_embed` – send yourself an embed with a button to generate or reveal your **Key** and **Sync Key** for plugin
  authentication.
- `/demibot_reset` – clear stored guild data and immediately rerun the setup wizard (server owner or administrator).
- `/demibot_clear` – purge all guild configuration and data (server owner only).

## Admin Setup Endpoints

The bot exposes setup endpoints for configuring Discord channels. All requests require an admin `X-Api-Key` header.

- `POST /api/admin/setup`
  - **Body:** `{ "channelId": "123", "type": "event" | "chat" }`
  - Registers a Discord channel as an event or chat channel.

## Extensibility

The bot exposes REST endpoints that can be expanded to mirror other bots or to provide additional game integrations.
