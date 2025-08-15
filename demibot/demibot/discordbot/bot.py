from __future__ import annotations

import importlib
import pkgutil
from pathlib import Path

import discord
from discord.ext import commands

from ..config import AppConfig


class DemiBot(commands.Bot):
    def __init__(self, cfg: AppConfig) -> None:
        intents = discord.Intents.all()
        super().__init__(command_prefix="!", intents=intents)
        self.cfg = cfg

    async def setup_hook(self) -> None:
        base = Path(__file__).parent / "cogs"
        for mod in pkgutil.iter_modules([str(base)]):
            await self.load_extension(f"{__package__}.cogs.{mod.name}")

        modules_base = Path(__file__).parent.parent / "modules"
        for mod in pkgutil.iter_modules([str(modules_base)]):
            await self.load_extension(f"{__package__}.modules.{mod.name}")


def create_bot(cfg: AppConfig) -> DemiBot:
    return DemiBot(cfg)
