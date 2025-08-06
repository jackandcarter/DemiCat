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

## Setup

### 1. Configure the bot
```
cd bot
npm install
npm start
```
On first run you will be prompted for the Discord bot credentials and MySQL connection details. They are stored in `bot/.env`.

### 2. Configure the Dalamud plugin
Update `dalamud-plugin/manifest.json` with the usual plugin metadata. In-game, open the plugin configuration and set the **Helper Base URL** if needed (defaults to `http://localhost:3000`).

## Building and Running

### Dalamud plugin
```bash
cd dalamud-plugin
dotnet build
```
Copy `bin/Debug/net6.0/dalamud-plugin.dll` into your Dalamud plugins folder and enable it.

## Usage

With the bot running and the plugin enabled, open the in-game **Events** window to view live Apollo embeds. Players can click the RSVP buttons to respond directly from FFXIV.

## Extensibility

The bot exposes REST endpoints that can be expanded to mirror other bots or to provide additional game integrations.
