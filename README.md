# DemiCat Monorepo

DemiCat connects Final Fantasy XIV with Discord by embedding Apollo event posts directly into the game.

```
DemiCat/
├── discord-helper/   # ASP.NET service that listens to Apollo event messages
└── dalamud-plugin/   # Dalamud plugin that renders the embeds in FFXIV
```

## Prerequisites

- [.NET 6 SDK](https://dotnet.microsoft.com/en-us/download)
- A Discord bot token and Apollo-managed channels
- FFXIV with the [Dalamud](https://github.com/goatcorp/Dalamud) plugin framework

## Setup

### 1. Configure the helper service
Create `discord-helper/appsettings.json`:

```json
{
  "BotToken": "YOUR_DISCORD_BOT_TOKEN",
  "BotId": 111111111111111111,
  "ChannelIds": [222222222222222222],
  "Port": 5000
}
```

- **BotToken** – token for your Discord bot.
- **BotId** – user ID of the Apollo bot.
- **ChannelIds** – Discord channel IDs the helper watches for event embeds.
- **Port** – HTTP port used by the helper service.

### 2. Configure the Dalamud plugin
Update `dalamud-plugin/manifest.json` with the usual plugin metadata. In-game, open the plugin configuration and set the **Helper Base URL** (e.g. `http://localhost:5000`) to match the port above.

## Building and Running

### Helper service
```bash
cd discord-helper && dotnet run
```

### Dalamud plugin
```bash
cd dalamud-plugin
 dotnet build
```
Copy `bin/Debug/net6.0/dalamud-plugin.dll` into your Dalamud plugins folder and enable it.

## Usage

With the helper running and the plugin enabled, open the in-game **Events** window to view live Apollo embeds. Players can click the RSVP buttons to respond directly from FFXIV.

## Extensibility

The helper service can expose additional REST endpoints or be adapted to mirror other bots. You can also replace Apollo entirely by swapping in your own event source.
