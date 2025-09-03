from __future__ import annotations

import json
from datetime import datetime

import discord
from discord.ext import commands
from sqlalchemy import select

from ...db.models import Presence as DbPresence
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
        data = StorePresence(
            id=member.id,
            name=member.display_name or member.name,
            status=self._status(member),
            avatar_url=str(member.display_avatar.url),
            roles=role_ids,
        )
        set_presence(member.guild.id, data)
        async with get_session() as db:
            stmt = select(DbPresence).where(
                DbPresence.guild_id == member.guild.id,
                DbPresence.user_id == member.id,
            )
            res = await db.execute(stmt)
            row = res.scalars().first()
            if row is None:
                db.add(
                    DbPresence(
                        guild_id=member.guild.id,
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


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(PresenceTracker(bot))
