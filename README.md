# DemiCat Monorepo
Please see the v1.2.2.1 branch for the latest info and stable build release.

## Developer Prerequisites

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


The build output `DemiCatPlugin.dll` can be found under `bin/Debug/net9.0/`. Copy it into your Dalamud plugins folder and enable it.

Alternatively, add this repository to Dalamud so it can install and update the plugin automatically:

1. In-game, open **Dalamud Settings → Experimental → Custom Repositories**.
2. Add `https://github.com/jackandcarter/DemiCat/raw/main/repo.json` (GitHub redirects automatically). Do **not** use `blob` links or the repository root.
3. Enable the **DemiCat** plugin from the available plugin list.


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
