# DemiCat Monorepo

DemiCat is a monorepo containing two projects that link Final Fantasy XIV (FFXIV) with Discord.

```
DemiCat/
├── discord-helper/   # Discord bot built with .NET 6
└── dalamud-plugin/   # Dalamud plugin loaded in-game
```

## Prerequisites

Install the following before working with the repository:

- [.NET 6 SDK](https://dotnet.microsoft.com/en-us/download) – required for both projects
- A Discord Bot token from the [Discord Developer Portal](https://discord.com/developers/applications)
- FFXIV with the [Dalamud](https://github.com/goatcorp/Dalamud) plugin framework

## Configuration

### `discord-helper/appsettings.json`

Create or edit `discord-helper/appsettings.json`:

```json
{
  "Discord": {
    "Token": "YOUR_DISCORD_BOT_TOKEN"
  }
}
```

### `dalamud-plugin/manifest.json`

Update `dalamud-plugin/manifest.json` with details about your plugin:

```json
{
  "Author": "Your Name",
  "Name": "DemiCat",
  "Description": "FFXIV plugin that communicates with a Discord bot.",
  "RepoUrl": "https://github.com/your/repo"
}
```

## Building and Running

### Discord Helper

```bash
cd discord-helper
 dotnet build       # build the Discord bot
 dotnet run         # run the bot locally
```

### Dalamud Plugin

```bash
cd dalamud-plugin
 dotnet build       # build the plugin DLL
```

Copy the built DLL from `bin/Debug` or `bin/Release` into your Dalamud plugins directory (e.g. `%APPDATA%\\XIVLauncher\\addons\\Hooks\\DemiCat` on Windows) and enable it through Dalamud's plugin installer.

## In-Game Usage

1. Run the Discord bot with `dotnet run` as described above.
2. Launch FFXIV via XIVLauncher with Dalamud enabled.
3. Install and enable the DemiCat plugin. Use `/demicat` commands in-game to send messages through the Discord bot.

## Extending DemiCat

- **Discord Helper** – add more bot commands or integrate other services.
- **Dalamud Plugin** – create new slash commands, UI elements or other in-game features.

Contributions that expand the interaction between the bot and plugin are welcome!
