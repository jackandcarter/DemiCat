import asyncio
import uvicorn

from .api import app, bot, config


async def main() -> None:
    bot_task = asyncio.create_task(bot.start_bot())
    config_host = config.get("api_host", "0.0.0.0")
    config_port = int(config.get("api_port", 8000))
    server = uvicorn.Server(uvicorn.Config(app, host=config_host, port=config_port, log_level="info"))
    api_task = asyncio.create_task(server.serve())
    await asyncio.gather(bot_task, api_task)


if __name__ == "__main__":
    asyncio.run(main())
