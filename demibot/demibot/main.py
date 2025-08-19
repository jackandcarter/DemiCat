from __future__ import annotations

"""Entry point for starting the DemiBot service."""

import argparse
import asyncio
import logging
import re
import sys

import uvicorn

from demibot import log_config

from .config import ensure_config
from .db.session import init_db
from .discordbot.bot import create_bot
from .http.api import create_app
from .repeat_events import recurring_event_poster


async def main_async() -> None:
    parser = argparse.ArgumentParser(description="Start the DemiBot service")
    parser.add_argument(
        "--reconfigure",
        action="store_true",
        help="Force interactive configuration prompts",
    )
    args = parser.parse_args()

    logging.basicConfig(level=logging.DEBUG)
    log_config.setup_logging()
    cfg = ensure_config(force_reconfigure=args.reconfigure)

    db_url = cfg.database.url
    masked_url = re.sub(r":[^:@/]+@", ":***@", db_url)
    logging.info("Initialising database at %s", masked_url)
    try:
        await init_db(db_url)
    except Exception:
        logging.exception("Database initialization failed")
        sys.exit(1)

    logging.info(
        "Starting FastAPI server on %s:%s",
        cfg.server.host,
        cfg.server.port,
    )
    try:
        app = create_app(cfg)
        bot = create_bot(cfg)
        config = uvicorn.Config(
            app, host=cfg.server.host, port=cfg.server.port, log_level="info"
        )
        server = uvicorn.Server(config)
        logging.info("ApiBaseUrl: http://%s:%s", cfg.server.host, cfg.server.port)
        await asyncio.gather(
            server.serve(),
            bot.start(cfg.discord_token),
            recurring_event_poster(),
        )
    except Exception:
        logging.exception("Failed to start services")
        sys.exit(1)


if __name__ == "__main__":
    asyncio.run(main_async())
