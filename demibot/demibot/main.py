import asyncio
import logging

import uvicorn

from .config import ensure_config
from .logging import setup_logging
from .db.session import create_engine
from .discordbot.bot import create_bot
from .http.api import create_app


async def start() -> None:
    cfg = ensure_config()
    setup_logging()
    engine = create_engine(cfg.database.url)

    bot = create_bot(cfg)
    app = create_app(cfg, engine)

    config = uvicorn.Config(app, host=cfg.server.host, port=cfg.server.port, log_level="info")
    server = uvicorn.Server(config)

    async with engine.begin():
        pass  # placeholder for migrations

    await asyncio.gather(
        bot.start(cfg.discord.token),
        server.serve(),
    )


def main() -> None:
    try:
        asyncio.run(start())
    except KeyboardInterrupt:
        logging.info("Shutting down")


if __name__ == "__main__":
    main()
