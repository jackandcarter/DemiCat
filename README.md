# DemiCat Monorepo

DemiCat connects Final Fantasy XIV with Discord by embedding Apollo event posts directly into the game.

```
DemiCat/
├── bot/             # Node.js Discord bot and REST interface
└── dalamud-plugin/  # Dalamud plugin that renders the embeds in FFXIV
```

## Prerequisites

- [Node.js 18+](https://nodejs.org/)
- A MySQL server
- A Discord bot token and Apollo-managed channels
- FFXIV with the [Dalamud](https://github.com/goatcorp/Dalamud) plugin framework

## Environment Variables

Copy `bot/.env.example` to `bot/.env` and fill in the required values.

- `DISCORD_TOKEN` – Discord bot token
- `DISCORD_APOLLO_BOT_ID` – Apollo bot ID used for event embeds
- `PORT` – HTTP server port (defaults to 3000)
- `DB_HOST`, `DB_USER`, `DB_PASSWORD`, `DB_NAME` – MySQL connection settings

## Setup

### 1. Configure the bot
```bash
cd bot
cp .env.example .env
npm i
node src/index.js
```

### 2. Configure the Dalamud plugin
Update `dalamud-plugin/manifest.json` with the usual plugin metadata. In-game, open the plugin configuration and set the **Helper Base URL** if needed (defaults to `http://localhost:3000`).

### 3. Insert API keys
Insert API keys into the `api_keys` table to authorize HTTP requests:

```sql
INSERT INTO api_keys (api_key, user_id, is_admin) VALUES ('your-api-key', 'discord-user-id', 1);
```
Use the value from `api_key` as the `X-Api-Key` header when calling the REST API.

## Building and Running

### Dalamud plugin
```bash
cd dalamud-plugin
dotnet build
```
Copy `bin/Debug/net6.0/dalamud-plugin.dll` into your Dalamud plugins folder and enable it.

## Usage

With the bot running and the plugin enabled, open the in-game **Events** window to view live Apollo embeds. Players can click the RSVP buttons to respond directly from FFXIV.

## Admin Setup Endpoints

The bot exposes setup endpoints for configuring Discord channels. All requests require an admin `X-Api-Key` header.

- `POST /api/admin/setup`
  - **Body:** `{ "channelId": "123", "type": "event" | "chat" }`
  - Registers a Discord channel as an event or chat channel.

## Extensibility

The bot exposes REST endpoints that can be expanded to mirror other bots or to provide additional game integrations.
