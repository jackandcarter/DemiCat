from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import ChatMessage
from ._messages_common import PostBody, fetch_messages, save_message

router = APIRouter(prefix="/api")


@router.get("/messages/{channel_id}", response_model=list[ChatMessage])
async def get_messages(
    channel_id: str,
    limit: int | None = None,
    before: str | None = None,
    after: str | None = None,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    return await fetch_messages(
        channel_id, ctx, db, is_officer=False, limit=limit, before=before, after=after
    )


@router.post("/messages")
async def post_message(
    body: PostBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    return await save_message(body, ctx, db, is_officer=False)
