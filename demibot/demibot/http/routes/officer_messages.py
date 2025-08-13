from __future__ import annotations

from datetime import datetime
from typing import List

from fastapi import APIRouter, Depends
from pydantic import BaseModel
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import ChatMessage
from ...db.models import Message

router = APIRouter(prefix="/api")


@router.get("/officer-messages/{channel_id}", response_model=List[ChatMessage])
async def get_officer_messages(
    channel_id: int,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> List[ChatMessage]:
    stmt = (
        select(Message)
        .where(Message.channel_id == channel_id, Message.is_officer)
        .order_by(Message.created_at.asc())
        .limit(50)
    )
    result = await db.execute(stmt)
    messages = result.scalars().all()
    return [
        ChatMessage(
            id=str(m.discord_message_id),
            channelId=str(m.channel_id),
            authorName=m.author_name,
            content=m.content_display,
            mentions=None,
        )
        for m in messages
    ]


class PostMessage(BaseModel):
    channelId: str
    content: str
    useCharacterName: bool = False


@router.post("/officer-messages")
async def post_officer_message(
    body: PostMessage,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> None:
    msg = Message(
        discord_message_id=int(datetime.utcnow().timestamp() * 1000),
        channel_id=int(body.channelId),
        guild_id=ctx.guild.id,
        author_id=ctx.user.id,
        author_name=ctx.user.global_name or "Unknown",
        content_raw=body.content,
        content_display=body.content,
        mentions_json=None,
        is_officer=True,
    )
    db.add(msg)
    await db.commit()
    return
