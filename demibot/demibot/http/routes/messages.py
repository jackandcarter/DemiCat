from __future__ import annotations

from datetime import datetime

from fastapi import APIRouter, Depends
from pydantic import BaseModel
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import ChatMessage
from ...db.models import Message

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
        out.append(
            ChatMessage(
                id=str(m.discord_message_id),
                channelId=str(m.channel_id),
                authorName=m.author_name,
                content=m.content_display,
            )
        )
    return [o.model_dump() for o in out]


@router.post("/messages")
async def post_message(
    body: PostBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    msg = Message(
        discord_message_id=int(datetime.utcnow().timestamp() * 1000),
        channel_id=int(body.channelId),
        guild_id=ctx.guild.id,
        author_id=ctx.user.id,
        author_name=ctx.user.global_name or "Player",
        content_raw=body.content,
        content_display=body.content,
        is_officer=False,
    )
    db.add(msg)
    await db.commit()
    return {"ok": True}
