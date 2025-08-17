from __future__ import annotations

"""Entry point for starting the DemiBot service."""

import argparse
import asyncio
from threading import Thread

import logging
import re
import sys

from demibot import log_config

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

    log_config.setup_logging()
    cfg = ensure_config(force_reconfigure=args.reconfigure)

    db_url = cfg.database.url
    masked_url = re.sub(r":[^:@/]+@", ":***@", db_url)
    logging.info("Initialising database at %s", masked_url)
    try:
        asyncio.run(init_db(db_url))
    except Exception:
        logging.exception("Database initialization failed")
        sys.exit(1)

    logging.info(
        "Starting Flask server on %s:%s",
        cfg.server.host,
        cfg.server.port,
    )
    try:
        app = create_app(cfg)
        flask_thread = Thread(
            target=_run_flask,
            args=(app, cfg.server.host, cfg.server.port),
            daemon=True,
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
