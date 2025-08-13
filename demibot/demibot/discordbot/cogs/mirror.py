from __future__ import annotations

import discord
from discord.ext import commands


class Mirror(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot

    @commands.Cog.listener()
    async def on_message(self, message: discord.Message) -> None:
        if message.author.bot:
            return
        # Placeholder: store message, process mentions, etc.
        

async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(Mirror(bot))
