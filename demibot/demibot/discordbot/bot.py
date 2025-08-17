from __future__ import annotations

import pkgutil
import logging
from pathlib import Path

import discord
from discord.ext import commands

from ..config import AppConfig

logger = logging.getLogger(__name__)


class DemiBot(commands.Bot):
    def __init__(self, cfg: AppConfig) -> None:
        intents = discord.Intents.all()
        super().__init__(command_prefix="!", intents=intents)
        self.cfg = cfg

    async def setup_hook(self) -> None:
        base = Path(__file__).parent / "cogs"
        for mod in pkgutil.iter_modules([str(base)]):
            module_path = f"{__package__}.cogs.{mod.name}"
            try:
                await self.load_extension(module_path)
            except Exception:
                logger.exception("Failed to load extension %s", module_path)
            else:
                logger.info("Loaded extension %s", module_path)

        modules_base = Path(__file__).parent.parent / "modules"
        for mod in pkgutil.iter_modules([str(modules_base)]):
            module_path = f"{__package__}.modules.{mod.name}"
            try:
                await self.load_extension(module_path)
            except Exception:
                logger.exception("Failed to load extension %s", module_path)
            else:
                logger.info("Loaded extension %s", module_path)

        try:
            synced = await self.tree.sync()
            logger.info("Synced %d global command(s)", len(synced))
            dev_guild_id = getattr(self.cfg, "dev_guild_id", None)
            if dev_guild_id:
                guild = discord.Object(id=dev_guild_id)
                self.tree.copy_global_to(guild=guild)
                guild_synced = await self.tree.sync(guild=guild)
                logger.info(
                    "Synced %d command(s) to guild %s", len(guild_synced), dev_guild_id
                )
        except Exception:
            logger.exception("Failed to sync application commands")

def create_bot(cfg: AppConfig) -> DemiBot:
    return DemiBot(cfg)
