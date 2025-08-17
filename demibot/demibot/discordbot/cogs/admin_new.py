from __future__ import annotations

import secrets

import discord
from discord import app_commands
from discord.ext import commands
from sqlalchemy import select

from ...db.models import Guild, Role, User, UserKey
from ...db.session import get_session


class AdminNew(commands.Cog):
    """Administrative commands using the new naming scheme."""

    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot


async def _authorized_role_ids(guild_id: int) -> set[int]:
    """Return a set of Discord role IDs authorized for the guild."""
    async for db in get_session():
        guild_res = await db.execute(
            select(Guild).where(Guild.discord_guild_id == guild_id)
        )
        guild = guild_res.scalars().first()
        if guild is None:
            return set()
        result = await db.execute(
            select(Role.id).where(
                Role.guild_id == guild.id,
                (Role.is_officer.is_(True) | Role.is_chat.is_(True)),
            )
        )
        return {row[0] for row in result}
    return set()


@app_commands.command(
    name="demibot_embed",
    description="Send an embed with a button to generate your Sync Key",
)
async def demibot_embed(interaction: discord.Interaction) -> None:
    """Send the Sync Key generation embed to authorized users."""
    if interaction.guild is None:
        await interaction.response.send_message("Guild only", ephemeral=True)
        return

    allowed_roles = await _authorized_role_ids(interaction.guild.id)
    member_role_ids = {r.id for r in interaction.user.roles}
    if not (member_role_ids & allowed_roles):
        await interaction.response.send_message("You are not authorized", ephemeral=True)
        return

    cfg = getattr(interaction.client, "cfg", None)
    image_url = None
    if cfg is not None:
        security = getattr(cfg, "security", cfg)
        image_url = getattr(security, "sync_image_url", None)

    embed = discord.Embed(
        title="Generate Sync Key",
        description=(
            "Use the button below to generate a **Sync Key** for the DemiCat plugin. "
            "This key links your Discord account with the bot."
        ),
    )
    if image_url:
        embed.set_image(url=image_url)

    class KeyView(discord.ui.View):
        def __init__(self, allowed: set[int]) -> None:
            super().__init__(timeout=180)
            self.allowed = allowed

        @discord.ui.button(label="Generate Key", style=discord.ButtonStyle.primary)
        async def generate(
            self, button_inter: discord.Interaction, button: discord.ui.Button
        ) -> None:
            if not (set(r.id for r in button_inter.user.roles) & self.allowed):
                await button_inter.response.send_message(
                    "You are not authorized", ephemeral=True
                )
                return

            token = secrets.token_hex(16)
            try:
                async for db in get_session():
                    guild_res = await db.execute(
                        select(Guild).where(
                            Guild.discord_guild_id == button_inter.guild.id
                        )
                    )
                    guild = guild_res.scalars().first()
                    if guild is None:
                        guild = Guild(
                            discord_guild_id=button_inter.guild.id,
                            name=button_inter.guild.name,
                        )
                        db.add(guild)
                        await db.flush()

                    user_res = await db.execute(
                        select(User).where(
                            User.discord_user_id == button_inter.user.id
                        )
                    )
                    user = user_res.scalars().first()
                    if user is None:
                        user = User(
                            discord_user_id=button_inter.user.id,
                            global_name=button_inter.user.global_name,
                            discriminator=button_inter.user.discriminator,
                        )
                        db.add(user)
                        await db.flush()

                    db.add(UserKey(user_id=user.id, guild_id=guild.id, token=token))
                    await db.commit()
            except Exception:
                await button_inter.response.send_message(
                    "Failed to generate key", ephemeral=True
                )
                return

            await button_inter.response.send_message(
                f"Your sync key: {token}", ephemeral=True
            )

    view = KeyView(allowed_roles)
    await interaction.response.send_message(embed=embed, view=view, ephemeral=True)


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(AdminNew(bot))
    bot.tree.add_command(demibot_embed)
