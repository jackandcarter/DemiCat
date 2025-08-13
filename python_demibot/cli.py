import argparse
import asyncio
import json
import logging

from .logging import setup_logging


logger = logging.getLogger(__name__)


def main():
    parser = argparse.ArgumentParser(description="DemiBot command line interface")
    parser.add_argument("--debug", action="store_true", help="Enable debug logging")
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("run", help="Run the Discord bot and API server")
    subparsers.add_parser("setup-db", help="Initialise the database")
    subparsers.add_parser("check-config", help="Validate configuration file")

    args = parser.parse_args()

    setup_logging(debug=args.debug)

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

    logger.info("Connecting to DB…")
    await db.connect()

    logger.info("Loading Discord bot…")
    bot_task = asyncio.create_task(bot.start_bot())

    logger.info("Starting API server…")
    host = config.get("api_host", "0.0.0.0")
    port = int(config.get("api_port", 8000))
    server = uvicorn.Server(uvicorn.Config(app, host=host, port=port, log_level="info"))
    api_task = asyncio.create_task(server.serve())

    await asyncio.gather(bot_task, api_task)


async def _setup_db() -> None:
    """Create database schema without running the server."""
    from .config import CONFIG_PATH
    from .database import Database

    logger.info("Loading configuration…")
    if not CONFIG_PATH.exists():
        logger.error("Configuration file not found at %s", CONFIG_PATH)
        return
    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        cfg = json.load(f)

    db = Database(cfg)

    logger.info("Connecting to DB…")
    await db.connect()
    logger.info("Database setup complete.")
    await db.close()


def _check_config() -> None:
    """Validate configuration file exists and contains required keys."""
    from .config import CONFIG_PATH, DEFAULT_CONFIG

    logger.info("Checking configuration…")
    if not CONFIG_PATH.exists():
        logger.error("Configuration file not found at %s", CONFIG_PATH)
        return

    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        cfg = json.load(f)

    missing = [k for k in DEFAULT_CONFIG if not cfg.get(k)]
    if missing:
        logger.warning("Missing config values: %s", ", ".join(missing))
    else:
        logger.info("Configuration looks good.")


if __name__ == "__main__":  # pragma: no cover
    main()
