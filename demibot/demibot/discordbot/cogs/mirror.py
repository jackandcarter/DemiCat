from __future__ import annotations

import asyncio
import json
import logging
import discord
from discord.ext import commands
from sqlalchemy import select
from sqlalchemy.exc import IntegrityError

from ...db.models import Embed, Guild, GuildChannel, Message
from ...db.session import get_session, init_db
from ...http.schemas import (
    ChatMessage,
    EmbedDto,
    EmbedFieldDto,
    EmbedButtonDto,
    EmbedAuthorDto,
    Mention,
    AttachmentDto,
)
from ...http.ws import manager


CHANNEL_SYNC_INTERVAL = 3600


class Mirror(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot
        # Ensure the database engine is available for this cog
        asyncio.create_task(init_db(bot.cfg.database.url))
        self._sync_task: asyncio.Task | None = None
        self._reconcile_lock = asyncio.Lock()

    async def cog_load(self) -> None:
        self.bot.loop.create_task(self._sync_guild_channels_once())
        self._sync_task = asyncio.create_task(self._channel_sync_loop())

    async def _sync_guild_channels_once(self) -> None:
        if hasattr(self.bot, "wait_until_ready"):
            await self.bot.wait_until_ready()
        await self._reconcile_channels()

    async def _channel_sync_loop(self) -> None:
        if hasattr(self.bot, "wait_until_ready"):
            await self.bot.wait_until_ready()
        while True:
            await asyncio.sleep(CHANNEL_SYNC_INTERVAL)
            try:
                await self._reconcile_channels()
            except Exception:
                logging.exception("Guild channel sync failed")

    async def _reconcile_channels(self) -> None:
        async with self._reconcile_lock:
            async for db in get_session():
                try:
                    for guild in self.bot.guilds:
                        result = await db.execute(
                            select(Guild).where(Guild.discord_guild_id == guild.id)
                        )
                        db_guild = result.scalar_one_or_none()
                        if db_guild is None:
                            db_guild = Guild(
                                discord_guild_id=guild.id, name=guild.name
                            )
                            db.add(db_guild)
                            await db.flush()
                        elif db_guild.name != guild.name:
                            db_guild.name = guild.name

                        existing = {
                            row.channel_id: row
                            for row in (
                                await db.execute(
                                    select(GuildChannel).where(
                                        GuildChannel.guild_id == db_guild.id
                                    )
                                )
                            ).scalars()
                        }

                        try:
                            channels = await guild.fetch_channels()
                        except Exception:
                            logging.exception(
                                "Failed to fetch channels for guild %s", guild.id
                            )
                            continue

                        for ch in channels:
                            if not hasattr(ch, "name"):
                                continue
                            existing_row = existing.get(ch.id)
                            if existing_row is None:
                                db.add(
                                    GuildChannel(
                                        guild_id=db_guild.id,
                                        channel_id=ch.id,
                                        kind="chat",
                                        name=ch.name,
                                    )
                                )
                            elif existing_row.name != ch.name:
                                existing_row.name = ch.name
                    await db.commit()
                except IntegrityError:
                    await db.rollback()
                    logging.exception("Guild channel reconciliation failed")
                break

    def cog_unload(self) -> None:
        if self._sync_task is not None:
            self._sync_task.cancel()

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
                    buttons: list[EmbedButtonDto] = []
                    try:
                        for row in getattr(message, "components", []) or []:
                            children = getattr(row, "children", None) or getattr(
                                row, "components", []
                            )
                            for comp in children or []:
                                if getattr(comp, "type", None) == 2:
                                    style = getattr(comp, "style", None)
                                    style_val = (
                                        style.value if hasattr(style, "value") else style
                                    )
                                    emoji = getattr(comp, "emoji", None)
                                    emoji_str = str(emoji) if emoji else None
                                    buttons.append(
                                        EmbedButtonDto(
                                            customId=getattr(comp, "custom_id", None),
                                            label=getattr(comp, "label", None),
                                            style=style_val,
                                            emoji=emoji_str,
                                        )
                                    )
                    except Exception:
                        logging.exception(
                            "Button extraction failed for guild %s channel %s message %s: %s",
                            getattr(message.guild, "id", None),
                            channel_id,
                            message.id,
                            data,
                        )
                        continue

                    try:
                        footer_data = data.get("footer", {})
                        provider_data = data.get("provider", {})
                        video_data = data.get("video", {})
                        author_list = []
                        first_author = data.get("author")
                        if first_author:
                            author_list.append(first_author)
                        author_list.extend(data.get("authors", []))
                        authors = [
                            EmbedAuthorDto(
                                name=a.get("name"),
                                url=a.get("url"),
                                iconUrl=a.get("icon_url"),
                            )
                            for a in author_list
                            if a
                        ] or None
                        dto = EmbedDto(
                            id=str(message.id),
                            timestamp=emb.timestamp,
                            color=emb.color.value if emb.color else None,
                            authorName=first_author.get("name") if first_author else None,
                            authorIconUrl=first_author.get("icon_url")
                            if first_author
                            else None,
                            authors=authors,
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
                            providerName=provider_data.get("name"),
                            providerUrl=provider_data.get("url"),
                            footerText=footer_data.get("text"),
                            footerIconUrl=footer_data.get("icon_url"),
                            videoUrl=video_data.get("url"),
                            videoWidth=video_data.get("width"),
                            videoHeight=video_data.get("height"),
                            buttons=buttons or None,
                            channelId=channel_id,
                            mentions=[m.id for m in message.mentions] or None,
                        )
                    except Exception:
                        logging.exception(
                            "Embed parsing failed for guild %s channel %s message %s: %s",
                            getattr(message.guild, "id", None),
                            channel_id,
                            message.id,
                            data,
                        )
                        continue
                    db.add(
                        Embed(
                            discord_message_id=message.id,
                            channel_id=channel_id,
                            guild_id=guild_id,
                            payload_json=json.dumps(dto.model_dump(mode="json")),
                            buttons_json=json.dumps(
                                [b.model_dump(mode="json") for b in buttons]
                            )
                            if buttons
                            else None,
                            source="apollo",
                        )
                    )
                    await manager.broadcast_text(
                        json.dumps(dto.model_dump(mode="json")),
                        guild_id,
                        officer_only=is_officer,
                        path="/ws/embeds",
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
            attachments_json = None
            if message.attachments:
                attachments_json = json.dumps(
                    [
                        {
                            "url": a.url,
                            "filename": a.filename,
                            "contentType": a.content_type,
                        }
                        for a in message.attachments
                    ]
                )

            db.add(
                Message(
                    discord_message_id=message.id,
                    channel_id=channel_id,
                    guild_id=guild_id,
                    author_id=message.author.id,
                    author_name=message.author.display_name
                    or message.author.name,
                    author_avatar_url=str(message.author.display_avatar.url)
                    if message.author.display_avatar
                    else None,
                    content_raw=message.content,
                    content_display=message.content,
                    attachments_json=attachments_json,
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

            attachments = [
                AttachmentDto(
                    url=a.url,
                    filename=a.filename,
                    contentType=a.content_type,
                )
                for a in message.attachments
            ] or None
            dto = ChatMessage(
                id=str(message.id),
                channelId=str(channel_id),
                authorName=message.author.display_name or message.author.name,
                authorAvatarUrl=str(message.author.display_avatar.url)
                if message.author.display_avatar
                else None,
                timestamp=message.created_at,
                content=message.content,
                attachments=attachments,
                mentions=mentions or None,
            )
            await manager.broadcast_text(
                json.dumps(dto.model_dump()),
                guild_id,
                officer_only=is_officer,
                path="/ws/messages",
            )
            break

    @commands.Cog.listener()
    async def on_message_edit(
        self, before: discord.Message, after: discord.Message
    ) -> None:
        channel_id = after.channel.id

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

            if after.author.bot:
                emb_row = await db.get(Embed, after.id)
                if emb_row is None:
                    return

                if not after.embeds:
                    return

                emb = after.embeds[0]
                data = emb.to_dict()

                buttons: list[EmbedButtonDto] = []
                try:
                    for row_comp in getattr(after, "components", []) or []:
                        children = getattr(row_comp, "children", None) or getattr(
                            row_comp, "components", []
                        )
                        for comp in children or []:
                            if getattr(comp, "type", None) == 2:
                                style = getattr(comp, "style", None)
                                style_val = (
                                    style.value if hasattr(style, "value") else style
                                )
                                emoji = getattr(comp, "emoji", None)
                                emoji_str = str(emoji) if emoji else None
                                buttons.append(
                                    EmbedButtonDto(
                                        customId=getattr(comp, "custom_id", None),
                                        label=getattr(comp, "label", None),
                                        style=style_val,
                                        emoji=emoji_str,
                                    )
                                )
                except Exception:
                    logging.exception(
                        "Button extraction failed for guild %s channel %s message %s: %s",
                        getattr(after.guild, "id", None),
                        channel_id,
                        after.id,
                        data,
                    )
                    return

                try:
                    footer_data = data.get("footer", {})
                    provider_data = data.get("provider", {})
                    video_data = data.get("video", {})
                    author_list: list[dict] = []
                    first_author = data.get("author")
                    if first_author:
                        author_list.append(first_author)
                    author_list.extend(data.get("authors", []))
                    authors = [
                        EmbedAuthorDto(
                            name=a.get("name"),
                            url=a.get("url"),
                            iconUrl=a.get("icon_url"),
                        )
                        for a in author_list
                        if a
                    ] or None

                    dto = EmbedDto(
                        id=str(after.id),
                        timestamp=emb.timestamp,
                        color=emb.color.value if emb.color else None,
                        authorName=first_author.get("name") if first_author else None,
                        authorIconUrl=first_author.get("icon_url") if first_author else None,
                        authors=authors,
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
                        providerName=provider_data.get("name"),
                        providerUrl=provider_data.get("url"),
                        footerText=footer_data.get("text"),
                        footerIconUrl=footer_data.get("icon_url"),
                        videoUrl=video_data.get("url"),
                        videoWidth=video_data.get("width"),
                        videoHeight=video_data.get("height"),
                        buttons=buttons or None,
                        channelId=channel_id,
                        mentions=[m.id for m in after.mentions] or None,
                    )
                except Exception:
                    logging.exception(
                        "Embed parsing failed for guild %s channel %s message %s: %s",
                        getattr(after.guild, "id", None),
                        channel_id,
                        after.id,
                        data,
                    )
                    return

                emb_row.payload_json = json.dumps(dto.model_dump(mode="json"))
                emb_row.buttons_json = (
                    json.dumps([b.model_dump(mode="json") for b in buttons])
                    if buttons
                    else None
                )
                await db.commit()

                await manager.broadcast_text(
                    json.dumps(dto.model_dump(mode="json")),
                    guild_id,
                    officer_only=is_officer,
                    path="/ws/embeds",
                )
            else:
                msg = await db.get(Message, after.id)
                if msg is None:
                    return

                attachments_json = None
                if after.attachments:
                    attachments_json = json.dumps(
                        [
                            {
                                "url": a.url,
                                "filename": a.filename,
                                "contentType": a.content_type,
                            }
                            for a in after.attachments
                        ]
                    )

                msg.content_raw = after.content
                msg.content_display = after.content
                msg.attachments_json = attachments_json
                await db.commit()

                mentions = [
                    Mention(id=str(m.id), name=m.display_name or m.name)
                    for m in after.mentions
                    if not m.bot
                ]

                attachments = [
                    AttachmentDto(
                        url=a.url,
                        filename=a.filename,
                        contentType=a.content_type,
                    )
                    for a in after.attachments
                ] or None

                dto = ChatMessage(
                    id=str(after.id),
                    channelId=str(channel_id),
                    authorName=after.author.display_name or after.author.name,
                    authorAvatarUrl=str(after.author.display_avatar.url)
                    if after.author.display_avatar
                    else None,
                    timestamp=after.created_at,
                    content=after.content,
                    attachments=attachments,
                    mentions=mentions or None,
                )
                await manager.broadcast_text(
                    json.dumps(dto.model_dump()),
                    guild_id,
                    officer_only=is_officer,
                    path="/ws/messages",
                )
            break

    @commands.Cog.listener()
    async def on_message_delete(self, message: discord.Message) -> None:
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

            if message.author.bot:
                emb_row = await db.get(Embed, message.id)
                if emb_row is not None:
                    await db.delete(emb_row)
                    await db.commit()

                await manager.broadcast_text(
                    json.dumps({"deletedId": str(message.id)}),
                    guild_id,
                    officer_only=is_officer,
                    path="/ws/embeds",
                )
            else:
                msg = await db.get(Message, message.id)
                if msg is not None:
                    await db.delete(msg)
                    await db.commit()

                await manager.broadcast_text(
                    json.dumps({"deletedId": str(message.id)}),
                    guild_id,
                    officer_only=is_officer,
                    path="/ws/messages",
                )
            break


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(Mirror(bot))
