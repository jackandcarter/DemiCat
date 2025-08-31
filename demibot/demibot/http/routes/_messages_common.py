from __future__ import annotations

from datetime import datetime
import json
import io

import discord
from fastapi import HTTPException, UploadFile
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
    messageReference: dict | None = None


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
                useCharacterName=getattr(author, "useCharacterName", False),
            )
        )
    return [o.model_dump() for o in out]


async def save_message(
    body: PostBody,
    ctx: RequestContext,
    db: AsyncSession,
    *,
    is_officer: bool,
    files: list[UploadFile] | None = None,
) -> dict:
    if is_officer and "officer" not in ctx.roles:
        raise HTTPException(status_code=403)
    channel_id = int(body.channelId)
    discord_msg_id: int | None = None
    attachments: list[AttachmentDto] | None = None
    channel = None
    if discord_client:
        channel = discord_client.get_channel(channel_id)
    if channel and isinstance(channel, discord.abc.Messageable):
        discord_files = None
        if files:
            discord_files = []
            for f in files:
                data = await f.read()
                discord_files.append(discord.File(io.BytesIO(data), filename=f.filename))
        # Build the identity prefix used when relaying to Discord
        prefix = ctx.user.global_name or ("Officer" if is_officer else "Player")
        if body.useCharacterName and ctx.user.character_name:
            prefix = f"{ctx.user.character_name} ({prefix})"
        try:
            sent = await channel.send(
                f"{prefix}: {body.content}",
                files=discord_files,
                reference=discord.MessageReference(
                    message_id=int(body.messageReference.get("messageId"))
                )
                if body.messageReference
                else None,
            )
            discord_msg_id = sent.id
            if sent.attachments:
                attachments = [
                    AttachmentDto(
                        url=a.url,
                        filename=a.filename,
                        contentType=a.content_type,
                    )
                    for a in sent.attachments
                ]
        except Exception:
            discord_msg_id = None
    if discord_msg_id is None:
        discord_msg_id = int(datetime.utcnow().timestamp() * 1000)
        if files:
            attachments = [
                AttachmentDto(
                    url=f"attachment://{f.filename}",
                    filename=f.filename,
                    contentType=f.content_type,
                )
                for f in files
            ]
    display_name = ctx.user.global_name or ("Officer" if is_officer else "Player")
    if body.useCharacterName and ctx.user.character_name:
        display_name = f"{ctx.user.character_name} ({display_name})"
    author = MessageAuthor(
        id=str(ctx.user.id),
        name=display_name,
        avatarUrl=None,
        useCharacterName=body.useCharacterName,
    )
    attachments_json = (
        json.dumps([a.model_dump() for a in attachments]) if attachments else None
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
        attachments_json=attachments_json,
        reference_json=json.dumps(body.messageReference)
        if body.messageReference
        else None,
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
        attachments=attachments,
        reference=body.messageReference,
        author=author,
        editedTimestamp=msg.edited_timestamp,
        useCharacterName=body.useCharacterName,
    )
    await manager.broadcast_text(
        dto.model_dump_json(),
        ctx.guild.id,
        officer_only=is_officer,
        path="/ws/officer-messages" if is_officer else "/ws/messages",
    )
    return {"ok": True, "id": str(discord_msg_id)}


async def edit_message(
    channel_id: str,
    message_id: str,
    content: str,
    ctx: RequestContext,
    db: AsyncSession,
    *,
    is_officer: bool,
) -> dict:
    if is_officer and "officer" not in ctx.roles:
        raise HTTPException(status_code=403)
    msg = await db.get(Message, int(message_id))
    if not msg or msg.channel_id != int(channel_id) or msg.is_officer != is_officer:
        raise HTTPException(status_code=404)
    if msg.author_id != ctx.user.id:
        raise HTTPException(status_code=403)
    msg.content_raw = msg.content_display = msg.content = content
    msg.edited_timestamp = datetime.utcnow()
    await db.commit()
    if discord_client:
        channel = discord_client.get_channel(int(channel_id))
        if channel and isinstance(channel, discord.abc.Messageable):
            try:
                discord_msg = await channel.fetch_message(int(message_id))
                await discord_msg.edit(content=content)
            except Exception:
                pass
    author = None
    if msg.author_json:
        try:
            author = MessageAuthor(**json.loads(msg.author_json))
        except Exception:
            author = None
    dto = ChatMessage(
        id=str(message_id),
        channelId=str(channel_id),
        authorName=msg.author_name,
        authorAvatarUrl=msg.author_avatar_url,
        timestamp=msg.created_at,
        content=content,
        editedTimestamp=msg.edited_timestamp,
        author=author,
        useCharacterName=getattr(author, "useCharacterName", False),
    )
    await manager.broadcast_text(
        dto.model_dump_json(),
        ctx.guild.id,
        officer_only=is_officer,
        path="/ws/officer-messages" if is_officer else "/ws/messages",
    )
    return {"ok": True}


async def delete_message(
    channel_id: str,
    message_id: str,
    ctx: RequestContext,
    db: AsyncSession,
    *,
    is_officer: bool,
) -> dict:
    if is_officer and "officer" not in ctx.roles:
        raise HTTPException(status_code=403)
    msg = await db.get(Message, int(message_id))
    if not msg or msg.channel_id != int(channel_id) or msg.is_officer != is_officer:
        raise HTTPException(status_code=404)
    if msg.author_id != ctx.user.id:
        raise HTTPException(status_code=403)
    await db.delete(msg)
    await db.commit()
    if discord_client:
        channel = discord_client.get_channel(int(channel_id))
        if channel and isinstance(channel, discord.abc.Messageable):
            try:
                discord_msg = await channel.fetch_message(int(message_id))
                await discord_msg.delete()
            except Exception:
                pass
    await manager.broadcast_text(
        json.dumps({"id": str(message_id), "channelId": str(channel_id), "deleted": True}),
        ctx.guild.id,
        officer_only=is_officer,
        path="/ws/officer-messages" if is_officer else "/ws/messages",
    )
    return {"ok": True}
