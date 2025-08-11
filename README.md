# DemiCat Monorepo

DemiCat connects Final Fantasy XIV with Discord by embedding Apollo event posts directly into the game.

```
DemiCat/
├── discord-demibot/  # Node.js Discord bot and REST interface
└── DemiCatPlugin/    # Dalamud plugin that renders the embeds in FFXIV
```

## Prerequisites

- [Node.js 18+](https://nodejs.org/)
- A MySQL server
- A Discord bot token and Apollo-managed channels
- FFXIV with the [Dalamud](https://github.com/goatcorp/Dalamud) plugin framework

## Environment Variables

Copy `discord-demibot/.env.example` to `discord-demibot/.env` and fill in each value.

- `DISCORD_BOT_TOKEN` – Discord bot token
- `DISCORD_CLIENT_ID` – Application client ID
- `APOLLO_BOT_ID` – Apollo bot ID used for event embeds
- `PLUGIN_PORT` – HTTP server port (defaults to 3000)
- `DB_HOST`, `DB_USER`, `DB_PASSWORD`, `DB_NAME` – MySQL connection settings

## Setup

### 1. Initialize the database
```bash
python database/setup.py
```
The script prompts for MySQL host, port, user, and password. It will create a `DemiBot` database, apply `database/schema.sql`,
and run any pending migrations. Re-run the script after pulling updates to ensure the schema (e.g. the `server_id` column on
`users`) stays current. Use `--local` to quickly target a local MySQL server.

### 2. Configure and start the bot
```bash
cd discord-demibot
cp .env.example .env
# Populate .env with the required environment variables
npm install
npm start
```
Ensure `.env` is populated with all required values before running `npm start`.

### 3. Configure the Dalamud plugin
Update `DemiCatPlugin/DemiCatPlugin.json` with the usual plugin metadata. In-game, open the plugin configuration and set the
**Helper Base URL** if needed (defaults to `http://localhost:3000`).

Run `/demibot_embed` in your Discord server and use the button to generate or view your **Key** and **Sync Key** (guild ID).
Enter both values in the plugin settings and press **Connect/Sync** (or **Validate** if you already have a key) to link the
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
