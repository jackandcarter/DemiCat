from __future__ import annotations

import secrets

import discord
from discord import app_commands
from discord.ext import commands

from .setup_wizard import demi


class KeyGen(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot


@demi.command(name="key", description="Generate an API key")
async def key_command(interaction: discord.Interaction) -> None:
    token = secrets.token_hex(16)
    try:
        await interaction.user.send(f"Your API key: {token}")
        await interaction.response.send_message("Sent you a DM with your key", ephemeral=True)
    except discord.Forbidden:
        await interaction.response.send_message("Unable to send DM", ephemeral=True)


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(KeyGen(bot))
