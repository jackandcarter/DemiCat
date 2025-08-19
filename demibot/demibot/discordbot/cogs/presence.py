from __future__ import annotations

import json
import discord
from discord.ext import commands

from ...http.ws import manager
from ..presence_store import Presence, set_presence


class PresenceTracker(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot

    def _status(self, member: discord.Member) -> str:
        status = str(member.status)
        if status in ("offline", "invisible"):
            return "offline"
        return "online"

    def _update(self, member: discord.Member) -> dict[str, str]:
        data = Presence(
            id=member.id,
            name=member.display_name or member.name,
            status=self._status(member),
        )
        set_presence(member.guild.id, data)
        return {"id": str(member.id), "name": data.name, "status": data.status}

    @commands.Cog.listener()
    async def on_ready(self) -> None:
        for guild in self.bot.guilds:
            for member in guild.members:
                self._update(member)

    @commands.Cog.listener()
    async def on_presence_update(
        self, before: discord.Member, after: discord.Member
    ) -> None:
        payload = self._update(after)
        await manager.broadcast_text(
            json.dumps(payload), after.guild.id, path="/ws/presences"
        )


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(PresenceTracker(bot))
