from __future__ import annotations

import discord
from discord import app_commands
from discord.ext import commands


demi = app_commands.Group(name="demi", description="DemiBot commands")


@demi.command(name="status", description="Show current status")
async def status(interaction: discord.Interaction) -> None:
    await interaction.response.send_message("DemiBot is running", ephemeral=True)


class SetupWizard(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(SetupWizard(bot))
    bot.tree.add_command(demi)
