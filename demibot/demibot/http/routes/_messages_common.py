from __future__ import annotations

from datetime import datetime
import json

import discord
from fastapi import HTTPException
from pydantic import BaseModel
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext
from ..schemas import ChatMessage, AttachmentDto, MessageAuthor, Mention
from ..ws import manager
from ...db.models import Message
from ..discord_client import discord_client


class PostBody(BaseModel):
    channelId: str
    content: str
    useCharacterName: bool | None = False


async def fetch_messages(
    channel_id: str,
    ctx: RequestContext,
    db: AsyncSession,
    *,
    is_officer: bool,
    limit: int | None = None,
    before: str | None = None,
    after: str | None = None,
) -> list[dict]:
    if is_officer and "officer" not in ctx.roles:
        raise HTTPException(status_code=403)
    stmt = select(Message).where(
        Message.channel_id == int(channel_id),
        Message.is_officer.is_(is_officer),
    )
    if before is not None:
        stmt = stmt.where(Message.discord_message_id < int(before))
    if after is not None:
        stmt = stmt.where(Message.discord_message_id > int(after))
    stmt = stmt.order_by(Message.created_at.desc())
    if limit is not None:
        stmt = stmt.limit(limit)
    result = await db.execute(stmt)
    rows = list(result.scalars())
    rows.reverse()
    out: list[ChatMessage] = []
    for m in rows:
        attachments = None
        if m.attachments_json:
            try:
                data = json.loads(m.attachments_json)
                attachments = [AttachmentDto(**a) for a in data]
            except Exception:
                attachments = None

        mentions = None
        if m.mentions_json:
            try:
                data = json.loads(m.mentions_json)
                mentions = [Mention(**a) for a in data]
            except Exception:
                mentions = None

        author = None
        if m.author_json:
            try:
                author = MessageAuthor(**json.loads(m.author_json))
            except Exception:
                author = None

        embeds = None
        if m.embeds_json:
            try:
                embeds = json.loads(m.embeds_json)
            except Exception:
                embeds = None

        reference = None
        if m.reference_json:
            try:
                reference = json.loads(m.reference_json)
            except Exception:
                reference = None

        components = None
        if m.components_json:
            try:
                components = json.loads(m.components_json)
            except Exception:
                components = None

        out.append(
            ChatMessage(
                id=str(m.discord_message_id),
                channelId=str(m.channel_id),
                authorName=m.author_name,
                authorAvatarUrl=m.author_avatar_url,
                timestamp=m.created_at,
                content=m.content or m.content_display or m.content_raw,
                attachments=attachments,
                mentions=mentions,
                author=author,
                embeds=embeds,
                reference=reference,
                components=components,
                editedTimestamp=m.edited_timestamp,
            )
        )
    return [o.model_dump() for o in out]


async def save_message(
    body: PostBody,
    ctx: RequestContext,
    db: AsyncSession,
    *,
    is_officer: bool,
) -> dict:
    if is_officer and "officer" not in ctx.roles:
        raise HTTPException(status_code=403)
    channel_id = int(body.channelId)
    discord_msg_id: int | None = None
    if discord_client:
        channel = discord_client.get_channel(channel_id)
        if isinstance(channel, discord.abc.Messageable):
            sent = await channel.send(body.content)
            discord_msg_id = sent.id
    if discord_msg_id is None:
        discord_msg_id = int(datetime.utcnow().timestamp() * 1000)
    author = MessageAuthor(
        id=str(ctx.user.id),
        name=ctx.user.global_name or ("Officer" if is_officer else "Player"),
        avatarUrl=None,
    )
    msg = Message(
        discord_message_id=discord_msg_id,
        channel_id=channel_id,
        guild_id=ctx.guild.id,
        author_id=ctx.user.id,
        author_name=author.name,
        author_avatar_url=author.avatarUrl,
        content_raw=body.content,
        content_display=body.content,
        content=body.content,
        author_json=author.model_dump_json(),
        is_officer=is_officer,
    )
    db.add(msg)
    await db.commit()
    await db.refresh(msg)
    dto = ChatMessage(
        id=str(discord_msg_id),
        channelId=str(channel_id),
        authorName=msg.author_name,
        authorAvatarUrl=msg.author_avatar_url,
        timestamp=msg.created_at,
        content=msg.content or msg.content_display,
        author=author,
        editedTimestamp=msg.edited_timestamp,
    )
    await manager.broadcast_text(
        dto.model_dump_json(),
        ctx.guild.id,
        officer_only=is_officer,
        path="/ws/officer-messages" if is_officer else "/ws/messages",
    )
    return {"ok": True, "id": str(discord_msg_id)}
