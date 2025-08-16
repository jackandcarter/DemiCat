from __future__ import annotations

"""Entry point for starting the DemiBot service."""

import asyncio
from threading import Thread

import logging

from .config import ensure_config
from .db.session import init_db
from .discordbot.bot import create_bot
from .http.api import create_app


def _run_flask(app, host: str, port: int) -> None:
    app.run(host=host, port=port)


def main() -> None:
    logging.basicConfig(level=logging.INFO)
    cfg = ensure_config()

    logging.info("Initialising database")
    asyncio.run(init_db(cfg.database.url))

    logging.info("Starting Flask server on %s:%s", cfg.server.host, cfg.server.port)
    app = create_app(cfg)
    flask_thread = Thread(
        target=_run_flask, args=(app, cfg.server.host, cfg.server.port), daemon=True
    )
    flask_thread.start()

    logging.info("Starting Discord bot")
    bot = create_bot(cfg)
    asyncio.run(bot.start(cfg.discord_token))


if __name__ == "__main__":
    main()

