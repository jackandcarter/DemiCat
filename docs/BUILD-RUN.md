# Build & Run Instructions

This guide walks through compiling the Dalamud plugin and running the DemiBot backend that powers it.

## Prerequisites

- .NET SDK 9.0 (the repo’s `global.json` pins the toolchain and allows prerelease SDKs).
- Dalamud dev libraries installed locally; update `Directory.Build.props` if your `DalamudLibPath` differs.
- Python 3.11+ with pip for the DemiBot service.
- MySQL (or MariaDB) accessible to the bot, plus a Discord bot token with `GUILD_MESSAGES`, `GUILD_MEMBERS`, `GUILD_PRESENCES`, and `MESSAGE_CONTENT` intents.

## Build the Dalamud plugin

1. Restore dependencies:

   ```bash
   dotnet restore DemiCatPlugin/DemiCatPlugin.csproj
   ```

2. Build for your desired configuration (Debug/Release):

   ```bash
   dotnet build DemiCatPlugin/DemiCatPlugin.csproj -c Release
   ```

   The compiled plugin lands under `DemiCatPlugin/bin/<Configuration>/net9.0-windows`. Copy the output (and `DemiCatPlugin.json`) into your Dalamud plugin staging folder.

3. If you customize the Dalamud library path, edit `Directory.Build.props` or supply `-p:DalamudLibPath=...` during `dotnet build`.

## Run the DemiBot backend

1. Create a virtual environment and install dependencies:

   ```bash
   cd demibot
   python -m venv .venv
   source .venv/bin/activate
   pip install -e .
   ```

2. Generate/update the config file:

   ```bash
   python -m demibot.main --reconfigure
   ```

   Follow the prompts for server host/port, local vs. remote MySQL credentials, and the Discord bot token. The answers are stored at `~/.config/demibot/config.json`; rerun with `--reconfigure` anytime you need to change them.

3. Apply database migrations:

   ```bash
   alembic -c demibot/db/migrations/env.py upgrade head
   ```

   Re-run this after pulling schema changes.

4. Start the service:

   ```bash
   python -m demibot.main
   ```

   Uvicorn will host the FastAPI app while the Discord bot, recurring-event poster, channel-name resync, asset purge, and Syncshell cleanup workers run in parallel.

5. Point the Dalamud plugin at the bot’s `ApiBaseUrl` (default `http://127.0.0.1:5050`) and link an API key via `/demibot embed` in Discord.

## Tips

- Use the `/demi setup` slash command to assign channels and officer roles after the backend is running.
- Reapply Alembic migrations whenever you pull new migrations.
- Check the FastAPI `/health` endpoint to confirm the service is up before connecting the plugin.
