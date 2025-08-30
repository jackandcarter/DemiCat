from __future__ import annotations

"""Entry point for starting the DemiBot service."""

import argparse
import asyncio
import logging
import re
import sys

import uvicorn
import discord

from demibot import log_config

from .config import ensure_config
from .db.session import init_db
from .discordbot.bot import create_bot
from .http.api import create_app
from .http.discord_client import set_discord_client
from .repeat_events import recurring_event_poster
from .channel_names import channel_name_resync
from .asset_cleanup import purge_deleted_assets


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
    cfg = await ensure_config(force_reconfigure=args.reconfigure)

    profile = cfg.database.active()
    logging.info(
        "Active DB profile: host=%s port=%s user=%s database=%s use_remote=%s password=***",
        profile.host,
        profile.port,
        profile.user,
        profile.database,
        cfg.database.use_remote,
    )

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
        app = create_app()
        intents = discord.Intents.default()
        intents.message_content = True
        intents.reactions = True
        intents.members = True
        intents.presences = True
        bot = create_bot(cfg, intents=intents)
        set_discord_client(bot)
        config = uvicorn.Config(
            app, host=cfg.server.host, port=cfg.server.port, log_level="info"
        )
        server = uvicorn.Server(config)
        logging.info("ApiBaseUrl: http://%s:%s", cfg.server.host, cfg.server.port)
        await asyncio.gather(
            server.serve(),
            bot.start(cfg.discord_token),
            recurring_event_poster(),
            channel_name_resync(),
            purge_deleted_assets(),
        )
    except Exception:
        logging.exception("Failed to start services")
        sys.exit(1)


if __name__ == "__main__":
    asyncio.run(main_async())
