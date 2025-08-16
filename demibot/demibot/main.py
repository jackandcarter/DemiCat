from __future__ import annotations

"""Entry point for starting the DemiBot service."""

import argparse
import asyncio
from threading import Thread

import logging
import sys

from .config import ensure_config
from .db.session import init_db
from .discordbot.bot import create_bot
from .http.api import create_app


def _run_flask(app, host: str, port: int) -> None:
    try:
        app.run(host=host, port=port)
    except Exception:
        logging.exception("Flask server failed")
        sys.exit(1)


def main() -> None:
    parser = argparse.ArgumentParser(description="Start the DemiBot service")
    parser.add_argument(
        "--reconfigure",
        action="store_true",
        help="Force interactive configuration prompts",
    )
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO)
    cfg = ensure_config(force_reconfigure=args.reconfigure)

    logging.info("Initialising database")
    try:
        asyncio.run(init_db(cfg.database.url))
    except Exception:
        logging.exception("Database initialization failed")
        sys.exit(1)

    logging.info(
        "Starting Flask server on %s:%s", cfg.server.host, cfg.server.port
    )
    try:
        app = create_app(cfg)
        flask_thread = Thread(
            target=_run_flask, args=(app, cfg.server.host, cfg.server.port), daemon=True
        )
        flask_thread.start()
    except Exception:
        logging.exception("Failed to start Flask server")
        sys.exit(1)

    logging.info("Starting Discord bot")
    try:
        bot = create_bot(cfg)
        asyncio.run(bot.start(cfg.discord_token))
    except Exception:
        logging.exception("Failed to start Discord bot")
        sys.exit(1)


if __name__ == "__main__":
    main()

