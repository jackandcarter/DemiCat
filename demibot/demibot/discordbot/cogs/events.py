from __future__ import annotations

import discord
from discord import app_commands
from discord.ext import commands

from .setup_wizard import demi


class Events(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot


@demi.command(name="event", description="Create a simple event")
async def create_event(interaction: discord.Interaction) -> None:
    await interaction.response.send_message("Event created (placeholder)", ephemeral=True)


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(Events(bot))
