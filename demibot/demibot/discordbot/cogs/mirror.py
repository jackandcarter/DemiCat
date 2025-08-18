from __future__ import annotations

import asyncio
import json
import discord
from discord.ext import commands
from sqlalchemy import select

from ...db.models import Embed, GuildChannel, Message
from ...db.session import get_session, init_db
from ...http.schemas import ChatMessage, EmbedDto, EmbedFieldDto, Mention
from ...http.ws import manager


class Mirror(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot
        # Ensure the database engine is available for this cog
        asyncio.create_task(init_db(bot.cfg.database.url))

    @commands.Cog.listener()
    async def on_message(self, message: discord.Message) -> None:
        """Mirror Discord messages into the database and notify plugins.

        Messages are only recorded if the channel is registered in the
        ``guild_channels`` table.  Officer chat messages are flagged so that
        they can be broadcast only to officer clients.
        """

        channel_id = message.channel.id

        # Ignore messages from bots (including ourselves) unless they are Apollo
        if message.author.bot:
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

                stored = False
                for emb in message.embeds:
                    data = emb.to_dict()
                    footer = data.get("footer", {}).get("text", "").lower()
                    author = data.get("author", {}).get("name", "")
                    if "apollo" not in footer and author != "Apollo":
                        continue
                    dto = EmbedDto(
                        id=str(message.id),
                        timestamp=emb.timestamp,
                        color=emb.color.value if emb.color else None,
                        authorName=emb.author.name if emb.author else None,
                        authorIconUrl=str(emb.author.icon_url)
                        if emb.author and emb.author.icon_url
                        else None,
                        title=emb.title,
                        description=emb.description,
                        url=emb.url,
                        fields=[
                            EmbedFieldDto(name=f.name, value=f.value, inline=f.inline)
                            for f in emb.fields
                        ]
                        or None,
                        thumbnailUrl=emb.thumbnail.url if emb.thumbnail else None,
                        imageUrl=emb.image.url if emb.image else None,
                        buttons=None,
                        channelId=channel_id,
                        mentions=[m.id for m in message.mentions] or None,
                    )
                    db.add(
                        Embed(
                            discord_message_id=message.id,
                            channel_id=channel_id,
                            guild_id=guild_id,
                            payload_json=json.dumps(dto.model_dump()),
                            source="apollo",
                        )
                    )
                    await manager.broadcast_text(
                        json.dumps(dto.model_dump()),
                        guild_id,
                        officer_only=is_officer,
                    )
                    stored = True
                if stored:
                    await db.commit()
                break
            return

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
