from __future__ import annotations

import asyncio
import json
import logging
import os
import discord
from discord.ext import commands
from sqlalchemy import select
from sqlalchemy.exc import IntegrityError

from ...db.models import Embed, Guild, GuildChannel, Message
from ...db.session import get_session
from ...http.schemas import (
    EmbedButtonDto,
    EmbedDto,
    Mention,
    AttachmentDto,
    MessageAuthor,
)
from ...http.discord_helpers import (
    embed_to_dto,
    message_to_chat_message,
    components_to_dtos,
    reaction_to_dto,
    extract_embed_buttons,
)

from ...http.ws import manager


class ApolloHelper:
    """Utility helpers for Apollo embed detection."""

    APOLLO_APPLICATION_ID = int(os.getenv("APOLLO_APPLICATION_ID", "0"))

    @staticmethod
    def IsApolloMessage(message: discord.Message) -> bool:  # noqa: N802
        """Return True if the message appears to be from Apollo."""

        app_id = getattr(message, "application_id", None)
        if ApolloHelper.APOLLO_APPLICATION_ID and app_id == ApolloHelper.APOLLO_APPLICATION_ID:
            return True
        for emb in getattr(message, "embeds", []) or []:
            data = emb.to_dict()
            footer = (data.get("footer", {}) or {}).get("text", "")
            author = (data.get("author", {}) or {}).get("name", "")
            if "apollo" in footer.lower() or author == "Apollo":
                return True
        return False


CHANNEL_SYNC_INTERVAL = 3600


class Mirror(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot
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

        if ApolloHelper.IsApolloMessage(message):
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
                buttons = extract_embed_buttons(message)
                for emb in message.embeds:
                    data = emb.to_dict()
                    try:
                        dto = embed_to_dto(message, emb, buttons)
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

        if message.author.bot:
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

            mentions = [
                Mention(
                    id=str(m.id),
                    name=m.display_name or m.name,
                )
                for m in message.mentions
                if not m.bot
            ]
            mentions_json = (
                json.dumps([m.model_dump() for m in mentions]) if mentions else None
            )

            author = MessageAuthor(
                id=str(message.author.id),
                name=message.author.display_name or message.author.name,
                avatarUrl=str(message.author.display_avatar.url)
                if message.author.display_avatar
                else None,
            )

            embeds_json = None
            if message.embeds:
                buttons = extract_embed_buttons(message)
                embed_dtos: list[EmbedDto] = []
                for emb in message.embeds:
                    try:
                        embed_dtos.append(
                            embed_to_dto(message, emb, buttons or None)
                        )
                    except Exception:
                        continue
                embeds_json = (
                    json.dumps([e.model_dump(mode="json") for e in embed_dtos])
                    if embed_dtos
                    else None
                )
            reference_json = None
            if message.reference:
                reference_json = json.dumps(
                    {
                        "messageId": message.reference.message_id,
                        "channelId": message.reference.channel_id,
                        "guildId": message.reference.guild_id,
                    }
                )
            components_json = None
            if getattr(message, "components", None):
                try:
                    comps = components_to_dtos(message)
                    components_json = (
                        json.dumps([c.model_dump() for c in comps]) if comps else None
                    )
                except Exception:
                    components_json = None

            reactions_json = None
            if message.reactions:
                try:
                    reactions_json = json.dumps(
                        [reaction_to_dto(r).model_dump() for r in message.reactions]
                    )
                except Exception:
                    reactions_json = None

            db.add(
                Message(
                    discord_message_id=message.id,
                    channel_id=channel_id,
                    guild_id=guild_id,
                    author_id=message.author.id,
                    author_name=author.name,
                    author_avatar_url=author.avatarUrl,
                    content_raw=message.content,
                    content_display=message.content,
                    content=message.content,
                    attachments_json=attachments_json,
                    mentions_json=mentions_json,
                    author_json=author.model_dump_json(),
                    embeds_json=embeds_json,
                    reference_json=reference_json,
                    components_json=components_json,
                    reactions_json=reactions_json,
                    edited_timestamp=message.edited_at,
                    is_officer=is_officer,
                )
            )
            await db.commit()

            dto = message_to_chat_message(message)
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

                buttons = extract_embed_buttons(after)

                try:
                    dto = embed_to_dto(after, emb, buttons)
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

                mentions = [
                    Mention(id=str(m.id), name=m.display_name or m.name)
                    for m in after.mentions
                    if not m.bot
                ]
                mentions_json = (
                    json.dumps([m.model_dump() for m in mentions]) if mentions else None
                )

                author = MessageAuthor(
                    id=str(after.author.id),
                    name=after.author.display_name or after.author.name,
                    avatarUrl=str(after.author.display_avatar.url)
                    if after.author.display_avatar
                    else None,
                )

                embeds_json = None
                if after.embeds:
                    buttons = extract_embed_buttons(after)
                    embed_dtos: list[EmbedDto] = []
                    for emb in after.embeds:
                        try:
                            embed_dtos.append(
                                embed_to_dto(after, emb, buttons or None)
                            )
                        except Exception:
                            continue
                    embeds_json = (
                        json.dumps([e.model_dump(mode="json") for e in embed_dtos])
                        if embed_dtos
                        else None
                    )
                reference_json = None
                if after.reference:
                    reference_json = json.dumps(
                        {
                            "messageId": after.reference.message_id,
                            "channelId": after.reference.channel_id,
                            "guildId": after.reference.guild_id,
                        }
                    )
                components_json = None
                if getattr(after, "components", None):
                    try:
                        comps = components_to_dtos(after)
                        components_json = (
                            json.dumps([c.model_dump() for c in comps])
                            if comps
                            else None
                        )
                    except Exception:
                        components_json = None

                reactions_json = None
                if after.reactions:
                    try:
                        reactions_json = json.dumps(
                            [reaction_to_dto(r).model_dump() for r in after.reactions]
                        )
                    except Exception:
                        reactions_json = None

                msg.content_raw = after.content
                msg.content_display = after.content
                msg.content = after.content
                msg.attachments_json = attachments_json
                msg.mentions_json = mentions_json
                msg.author_name = author.name
                msg.author_avatar_url = author.avatarUrl
                msg.author_json = author.model_dump_json()
                msg.embeds_json = embeds_json
                msg.reference_json = reference_json
                msg.components_json = components_json
                msg.reactions_json = reactions_json
                msg.edited_timestamp = after.edited_at
                await db.commit()

                dto = message_to_chat_message(after)
                await manager.broadcast_text(
                    json.dumps(dto.model_dump()),
                    guild_id,
                    officer_only=is_officer,
                    path="/ws/messages",
                )
            break

    @commands.Cog.listener()
    async def on_reaction_add(
        self, reaction: discord.Reaction, user: discord.abc.User
    ) -> None:
        """Persist and broadcast reaction additions."""

        async for db in get_session():
            msg = await db.get(Message, reaction.message.id)
            if msg is None:
                break

            reactions_json = None
            try:
                reactions_json = json.dumps(
                    [reaction_to_dto(r).model_dump() for r in reaction.message.reactions]
                )
            except Exception:
                reactions_json = None
            msg.reactions_json = reactions_json
            await db.commit()

            dto = message_to_chat_message(reaction.message)
            await manager.broadcast_text(
                json.dumps(dto.model_dump()),
                msg.guild_id,
                officer_only=msg.is_officer,
                path="/ws/messages",
            )
            break

    @commands.Cog.listener()
    async def on_reaction_remove(
        self, reaction: discord.Reaction, user: discord.abc.User
    ) -> None:
        """Persist and broadcast reaction removals."""

        async for db in get_session():
            msg = await db.get(Message, reaction.message.id)
            if msg is None:
                break

            reactions_json = None
            try:
                reactions_json = json.dumps(
                    [reaction_to_dto(r).model_dump() for r in reaction.message.reactions]
                )
            except Exception:
                reactions_json = None
            msg.reactions_json = reactions_json
            await db.commit()

            dto = message_to_chat_message(reaction.message)
            await manager.broadcast_text(
                json.dumps(dto.model_dump()),
                msg.guild_id,
                officer_only=msg.is_officer,
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
