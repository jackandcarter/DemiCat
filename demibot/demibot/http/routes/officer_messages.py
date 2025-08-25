from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ._messages_common import PostBody, fetch_messages, save_message

router = APIRouter(prefix="/api")


@router.get("/officer-messages/{channel_id}")
async def get_officer_messages(
    channel_id: str,
    limit: int | None = None,
    before: str | None = None,
    after: str | None = None,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    return await fetch_messages(
        channel_id, ctx, db, is_officer=True, limit=limit, before=before, after=after
    )


@router.post("/officer-messages")
async def post_officer_message(
    body: PostBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    return await save_message(body, ctx, db, is_officer=True)
