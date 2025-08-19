from __future__ import annotations

from datetime import datetime
import json

import discord
from fastapi import APIRouter, Depends
from pydantic import BaseModel
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import ChatMessage, AttachmentDto
from ..ws import manager
from ...db.models import Message
from ..discord_client import discord_client

router = APIRouter(prefix="/api")


class PostBody(BaseModel):
    channelId: str
    content: str
    useCharacterName: bool | None = False


@router.get("/messages/{channel_id}")
async def get_messages(
    channel_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    stmt = (
        select(Message)
        .where(
            Message.channel_id == int(channel_id),
            Message.is_officer.is_(False),
        )
        .order_by(Message.created_at)
    )
    result = await db.execute(stmt)
    out: list[ChatMessage] = []
    for m in result.scalars():
        attachments = None
        if m.attachments_json:
            try:
                data = json.loads(m.attachments_json)
                attachments = [AttachmentDto(**a) for a in data]
            except Exception:
                attachments = None
        out.append(
            ChatMessage(
                id=str(m.discord_message_id),
                channelId=str(m.channel_id),
                authorName=m.author_name,
                authorAvatarUrl=m.author_avatar_url,
                timestamp=m.created_at,
                content=m.content_display,
                attachments=attachments,
            )
        )
    return [o.model_dump() for o in out]


@router.post("/messages")
async def post_message(
    body: PostBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    channel_id = int(body.channelId)
    discord_msg_id: int | None = None
    if discord_client:
        channel = discord_client.get_channel(channel_id)
        if isinstance(channel, discord.abc.Messageable):
            sent = await channel.send(body.content)
            discord_msg_id = sent.id

    if discord_msg_id is None:
        discord_msg_id = int(datetime.utcnow().timestamp() * 1000)

    msg = Message(
        discord_message_id=discord_msg_id,
        channel_id=channel_id,
        guild_id=ctx.guild.id,
        author_id=ctx.user.id,
        author_name=ctx.user.global_name or "Player",
        content_raw=body.content,
        content_display=body.content,
        is_officer=False,
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
        content=msg.content_display,
    )
    await manager.broadcast_text(
        json.dumps(dto.model_dump()), ctx.guild.id, officer_only=False
    )

    return {"ok": True, "id": str(discord_msg_id)}
