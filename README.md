# DemiCat Monorepo

DemiCat connects Final Fantasy XIV with Discord by using DemiBot's Server Core and APIs.

```
DemiCat/
├── demibot/         # Python Discord bot and REST interface
└── DemiCatPlugin/   # Dalamud plugin that renders the embeds in FFXIV
```

## Features

### Plugin Tabs

- **Events** – Browse and RSVP to Apollo events from within FFXIV.
- **Create** – Draft new events without leaving the game.
- **Templates** – Save and reuse preset event layouts.
- **Request Board** – Track signup requests and interest.
- **SyncShell** – Work-in-progress replacement for the Mare Synchronos mod-sharing plugin. Syncs Penumbra mod lists to replicate player appearances and is disabled by default while under development (see `DemiCatPlugin/SyncshellWindow.cs`).
- **FC Chat** – Mirror Discord conversations directly in game.
- **Officer** – Administrative tools for event staff and moderators.
  - Officer chat window now includes a padded area beneath the input box reserved for future features.

### DemiBot Services

- Vault ingestion and archival (`demibot/demibot/discordbot/cogs/vault.py`).
- Asset and bundle APIs (`demibot/demibot/http/routes/assets.py`, `demibot/demibot/http/routes/bundles.py`).
- Delta tokens for incremental sync (`demibot/demibot/http/routes/delta_token.py`).
- User settings endpoints (`demibot/demibot/http/routes/user_settings.py`).
- Privacy and data controls (`demibot/demibot/http/routes/users.py`).

## Prerequisites

- [.NET SDK 9+](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
- [Python 3.11+](https://www.python.org/)
- A database (SQLite by default, MySQL optional)
- A Discord bot token and Apollo-managed channels
- FFXIV with the [Dalamud](https://github.com/goatcorp/Dalamud) plugin framework

Optional tools for automated setup:
- [uv](https://github.com/astral-sh/uv) or [Homebrew](https://brew.sh/) for installing Python/.NET if missing

## Configuration

Settings for the bot are stored in `~/.config/demibot/config.json`. Create or
update this file by running the service with the reconfigure flag:

```bash
python -m demibot.main --reconfigure
```

The configuration file includes the database connection details, the Discord
bot token and server options such as the WebSocket path (default
`/ws/embeds`).

> **Security note:** `~/.config/demibot/config.json` contains the Discord token
> and database credentials. Treat this file as a secret and set restrictive
> permissions so only your user can read it (e.g. `chmod 600
> ~/.config/demibot/config.json`).

### Bot mirroring whitelist

By default, DemiBot ignores messages sent by other bots. To persist and broadcast
messages from specific bot accounts, set the `BOT_MIRROR_WHITELIST` environment
variable to a comma-separated list of Discord user IDs:

```bash
export BOT_MIRROR_WHITELIST="123456789012345678,987654321098765432"
```

Only bots with IDs in this whitelist are mirrored; all other bot messages
continue to be ignored.

All public HTTP APIs now use **camelCase** for JSON field names. Python and C# code may use snake_case or PascalCase internally, but requests and responses over the wire are consistently camelCase.


## Setup

Run the helper script to bootstrap both the Python and .NET parts of the
project. It verifies Python 3.11+ and the .NET 9 SDK are installed (using
`uv` or `brew` when available), creates a virtual environment, installs
dependencies from `demibot/pyproject.toml`, and builds the Dalamud plugin in
Release mode.

```bash
bash scripts/setup_env.sh [--unit-tests] [--integration-tests]
```

### 1. Install dependencies and initialize the database
```bash
cd demibot
python -m venv .venv
source .venv/bin/activate
pip install -e .
alembic -c demibot/db/migrations/env.py upgrade head
```
Re-run the migration command after pulling updates to apply any schema changes. See MIGRATIONS.md for naming guidelines.

### 2. Configure and start the bot
```bash
python -m demibot.main --reconfigure
```
The first run will prompt for required settings and write them to
`~/.config/demibot/config.json` before starting the Discord bot and HTTP API.

With the bot online, run `/demibot embed` in your Discord server to post a key
generation message. Members can click the button to receive their API key in an
ephemeral reply. Each user should generate their own key with this command for
plugin authentication.

### 3. Configure the Dalamud plugin
Update `DemiCatPlugin/DemiCatPlugin.json` with the usual plugin metadata. In-game,
open the plugin configuration and set the `ApiBaseUrl` if needed (default
`http://127.0.0.1:5050`).

Use the API key obtained from `/demibot embed` and enter it in the plugin
**Settings** window under **API Key**. Press **Sync** to validate the key and
link the plugin with the bot.

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

WebSocket communication now streams data in 1 KB chunks and continues reading until the end of each message, allowing the plugin to handle payloads larger than the previous 16-byte limit.

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
- `/demibot embed` – post an embed with a button to generate or reveal your API key for plugin authentication.
- `/demibot_reset` – clear stored guild data and immediately rerun the setup wizard (server owner or administrator).
- `/demibot_clear` – purge all guild configuration and data (server owner only).

## Admin Setup Endpoints

The bot exposes setup endpoints for configuring Discord channels. All requests require an admin `X-Api-Key` header.

- `POST /api/admin/setup`
  - **Body:** `{ "channelId": "123", "type": "event" | "chat" }`
  - Registers a Discord channel as an event or chat channel.

## Extensibility

The bot exposes REST endpoints that can be expanded to mirror other bots or to provide additional game integrations.
