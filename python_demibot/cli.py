import argparse
import asyncio
import json


def main():
    parser = argparse.ArgumentParser(description="DemiBot command line interface")
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("run", help="Run the Discord bot and API server")
    subparsers.add_parser("setup-db", help="Initialise the database")
    subparsers.add_parser("check-config", help="Validate configuration file")

    args = parser.parse_args()

    if args.command == "run":
        asyncio.run(_run())
    elif args.command == "setup-db":
        asyncio.run(_setup_db())
    elif args.command == "check-config":
        _check_config()


async def _run() -> None:
    """Start the bot and API server."""
    from .api import app, bot, config, db
    import uvicorn

    print("Connecting to DB…")
    await db.connect()

    print("Loading Discord bot…")
    bot_task = asyncio.create_task(bot.start_bot())

    print("Starting API server…")
    host = config.get("api_host", "0.0.0.0")
    port = int(config.get("api_port", 8000))
    server = uvicorn.Server(uvicorn.Config(app, host=host, port=port, log_level="info"))
    api_task = asyncio.create_task(server.serve())

    await asyncio.gather(bot_task, api_task)


async def _setup_db() -> None:
    """Create database schema without running the server."""
    from .config import CONFIG_PATH
    from .database import Database

    print("Loading configuration…")
    if not CONFIG_PATH.exists():
        print(f"Configuration file not found at {CONFIG_PATH}")
        return
    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        cfg = json.load(f)

    db = Database(cfg)

    print("Connecting to DB…")
    await db.connect()
    print("Database setup complete.")
    await db.close()


def _check_config() -> None:
    """Validate configuration file exists and contains required keys."""
    from .config import CONFIG_PATH, DEFAULT_CONFIG

    print("Checking configuration…")
    if not CONFIG_PATH.exists():
        print(f"Configuration file not found at {CONFIG_PATH}")
        return

    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        cfg = json.load(f)

    missing = [k for k in DEFAULT_CONFIG if not cfg.get(k)]
    if missing:
        print("Missing config values: " + ", ".join(missing))
    else:
        print("Configuration looks good.")


if __name__ == "__main__":  # pragma: no cover
    main()
