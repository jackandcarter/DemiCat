from __future__ import annotations

import secrets

import discord
from discord import app_commands
from discord.ext import commands
from sqlalchemy import select

from ...db.models import Guild, User, UserKey
from ...db.session import get_session
from .setup_wizard import demi


class KeyGen(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot


@demi.command(name="key", description="Generate an API key")
async def key_command(interaction: discord.Interaction) -> None:
    token = secrets.token_hex(16)
    try:
        async for db in get_session():
            guild_res = await db.execute(
                select(Guild).where(Guild.discord_guild_id == interaction.guild.id)
            )
            guild = guild_res.scalars().first()
            if guild is None:
                guild = Guild(
                    discord_guild_id=interaction.guild.id,
                    name=interaction.guild.name,
                )
                db.add(guild)
                await db.flush()

            user_res = await db.execute(
                select(User).where(User.discord_user_id == interaction.user.id)
            )
            user = user_res.scalars().first()
            if user is None:
                user = User(
                    discord_user_id=interaction.user.id,
                    global_name=interaction.user.global_name,
                    discriminator=interaction.user.discriminator,
                )
                db.add(user)
                await db.flush()

            db.add(UserKey(user_id=user.id, guild_id=guild.id, token=token))
            await db.commit()
    except Exception:
        await interaction.response.send_message(
            "Failed to generate key", ephemeral=True
        )
        return

    try:
        await interaction.user.send(f"Your API key: {token}")
        await interaction.response.send_message(
            "Sent you a DM with your key", ephemeral=True
        )
    except discord.Forbidden:
        await interaction.response.send_message("Unable to send DM", ephemeral=True)


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(KeyGen(bot))
