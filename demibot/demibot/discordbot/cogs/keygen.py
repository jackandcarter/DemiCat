from __future__ import annotations

import secrets

import discord
from discord import app_commands
from discord.ext import commands
from sqlalchemy import delete, select

from ...db.models import (
    Guild,
    GuildConfig,
    Membership,
    MembershipRole,
    Role,
    User,
    UserKey,
)
from ...db.session import get_session
from ..utils import is_premium_subscriber_role
from .setup_wizard import demi


class KeyGen(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot


@demi.command(name="key", description="Generate an API key")
async def key_command(interaction: discord.Interaction) -> None:
    token = secrets.token_hex(16)
    try:
        async with get_session() as db:
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
            member_roles = [r for r in interaction.user.roles if r.name != "@everyone"]
            roles_cached = ",".join(str(r.id) for r in member_roles)

            membership_res = await db.execute(
                select(Membership).where(
                    Membership.guild_id == guild.id,
                    Membership.user_id == user.id,
                )
            )
            membership = membership_res.scalars().first()
            if membership is None:
                membership = Membership(guild_id=guild.id, user_id=user.id)
                db.add(membership)
                await db.flush()

            # map existing roles for the guild
            role_res = await db.execute(select(Role).where(Role.guild_id == guild.id))
            role_map: dict[int, Role] = {
                r.discord_role_id: r for r in role_res.scalars()
            }

            cfg_res = await db.execute(
                select(GuildConfig).where(GuildConfig.guild_id == guild.id)
            )
            cfg = cfg_res.scalars().first()
            officer_role_ids = (
                {int(rid) for rid in cfg.officer_role_ids.split(",") if rid}
                if cfg and cfg.officer_role_ids
                else set()
            )

            for r in member_roles:
                mapped = role_map.get(r.id)
                is_officer = r.id in officer_role_ids
                premium_subscriber = is_premium_subscriber_role(r)
                if mapped is None:
                    mapped = Role(
                        guild_id=guild.id,
                        discord_role_id=r.id,
                        name=r.name,
                        position=r.position,
                        hoist=r.hoist,
                        premium_subscriber=premium_subscriber,
                        is_officer=is_officer,
                    )
                    db.add(mapped)
                    await db.flush()
                    role_map[r.id] = mapped
                else:
                    mapped.name = r.name
                    mapped.is_officer = is_officer
                    mapped.position = r.position
                    mapped.hoist = r.hoist
                    mapped.premium_subscriber = premium_subscriber

            await db.execute(
                delete(MembershipRole).where(
                    MembershipRole.membership_id == membership.id
                )
            )

            for r in member_roles:
                mapped = role_map.get(r.id)
                if mapped is None:
                    continue
                db.add(
                    MembershipRole(membership_id=membership.id, role_id=mapped.id)
                )
            await db.execute(
                delete(UserKey).where(
                    UserKey.user_id == user.id,
                    UserKey.guild_id == guild.id,
                )
            )

            db.add(
                UserKey(
                    user_id=user.id,
                    guild_id=guild.id,
                    token=token,
                    roles_cached=roles_cached,
                )
            )
            await db.commit()
    except Exception:
        await interaction.response.send_message(
            "Failed to generate key", ephemeral=True
        )
        return

    embed = discord.Embed(title="Your API key", description=token)
    try:
        await interaction.response.send_message(embed=embed, ephemeral=True)
    except Exception:
        if interaction.response.is_done():
            await interaction.followup.send("Failed to generate key", ephemeral=True)
        else:
            await interaction.response.send_message("Failed to generate key", ephemeral=True)


@app_commands.command(name="generatekey", description="Generate an API key")
async def generatekey(interaction: discord.Interaction) -> None:
    await key_command.callback(interaction)


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(KeyGen(bot))
    bot.tree.add_command(generatekey)
