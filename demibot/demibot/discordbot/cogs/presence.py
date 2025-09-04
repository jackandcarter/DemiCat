from __future__ import annotations

import json
from datetime import datetime

import discord
from discord.ext import commands
from sqlalchemy import select

from ...db.models import Guild, Membership, Presence as DbPresence
from ...db.session import get_session
from ...http.ws import manager
from ..presence_store import Presence as StorePresence, set_presence


class PresenceTracker(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot

    def _status(self, member: discord.Member) -> str:
        status = str(member.status)
        if status in ("offline", "invisible"):
            return "offline"
        return "online"

    async def _update(self, member: discord.Member) -> dict[str, str | None]:
        role_ids = [r.id for r in member.roles if r.name != "@everyone"]
        display_name = member.display_name or member.name
        avatar_url = str(member.display_avatar.url)
        data = StorePresence(
            id=member.id,
            name=display_name,
            status=self._status(member),
            avatar_url=avatar_url,
            roles=role_ids,
        )
        set_presence(member.guild.id, data)
        async with get_session() as db:
            # Look up or create the internal guild record so that we can use its
            # database id for related records. The Membership and Presence tables
            # reference Guild.id rather than the Discord guild id.
            guild_res = await db.execute(
                select(Guild).where(Guild.discord_guild_id == member.guild.id)
            )
            guild_row = guild_res.scalar_one_or_none()
            if guild_row is None:
                guild_row = Guild(
                    discord_guild_id=member.guild.id, name=member.guild.name
                )
                db.add(guild_row)
                await db.flush()

            mem_stmt = select(Membership).where(
                Membership.guild_id == guild_row.id,
                Membership.user_id == member.id,
            )
            mem_res = await db.execute(mem_stmt)
            mem = mem_res.scalars().first()
            if mem is None:
                mem = Membership(guild_id=guild_row.id, user_id=member.id)
                db.add(mem)
            mem.nickname = display_name
            mem.avatar_url = avatar_url

            stmt = select(DbPresence).where(
                DbPresence.guild_id == guild_row.id,
                DbPresence.user_id == member.id,
            )
            res = await db.execute(stmt)
            row = res.scalars().first()
            if row is None:
                db.add(
                    DbPresence(
                        guild_id=guild_row.id,
                        user_id=member.id,
                        status=data.status,
                    )
                )
            else:
                row.status = data.status
                row.updated_at = datetime.utcnow()
            await db.commit()
        return {
            "id": str(member.id),
            "name": data.name,
            "status": data.status,
            "avatar_url": data.avatar_url,
            "roles": [str(r) for r in role_ids],
        }

    @commands.Cog.listener()
    async def on_ready(self) -> None:
        for guild in self.bot.guilds:
            for member in guild.members:
                await self._update(member)

    @commands.Cog.listener()
    async def on_presence_update(
        self, before: discord.Member, after: discord.Member
    ) -> None:
        payload = await self._update(after)
        await manager.broadcast_text(
            json.dumps(payload), after.guild.id, path="/ws/presences"
        )

    @commands.Cog.listener()
    async def on_member_update(
        self, before: discord.Member, after: discord.Member
    ) -> None:
        payload = await self._update(after)
        await manager.broadcast_text(
            json.dumps(payload), after.guild.id, path="/ws/presences"
        )


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(PresenceTracker(bot))
