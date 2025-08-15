from __future__ import annotations

import json
import discord
from discord.ext import commands
from sqlalchemy import select

from ...db.models import GuildChannel, Message
from ...db.session import create_engine, get_session
from ...http.schemas import ChatMessage, Mention
from ...http.ws import manager


class Mirror(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot
        # Ensure the database engine is available for this cog
        create_engine(bot.cfg.database.url)

    @commands.Cog.listener()
    async def on_message(self, message: discord.Message) -> None:
        """Mirror Discord messages into the database and notify plugins.

        Messages are only recorded if the channel is registered in the
        ``guild_channels`` table.  Officer chat messages are flagged so that
        they can be broadcast only to officer clients.
        """

        # Ignore messages from bots (including ourselves)
        if message.author.bot:
            return

        channel_id = message.channel.id

        async for db in get_session():
            result = await db.execute(
                select(GuildChannel.kind, GuildChannel.guild_id).where(
                    GuildChannel.channel_id == channel_id
                )
            )
            row = result.one_or_none()
            if row is None:
                return  # channel not registered

            kind, guild_id = row
            is_officer = kind == "officer_chat"

            # Persist the message
            db.add(
                Message(
                    discord_message_id=message.id,
                    channel_id=channel_id,
                    guild_id=guild_id,
                    author_id=message.author.id,
                    author_name=message.author.display_name
                    or message.author.name,
                    content_raw=message.content,
                    content_display=message.content,
                    is_officer=is_officer,
                )
            )
            await db.commit()

            mentions = [
                Mention(
                    id=str(m.id),
                    name=m.display_name or m.name,
                )
                for m in message.mentions
                if not m.bot
            ]

            dto = ChatMessage(
                id=str(message.id),
                channelId=str(channel_id),
                authorName=message.author.display_name or message.author.name,
                content=message.content,
                mentions=mentions or None,
            )
            await manager.broadcast_text(
                json.dumps(dto.model_dump()), guild_id, officer_only=is_officer
            )
            break


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(Mirror(bot))
