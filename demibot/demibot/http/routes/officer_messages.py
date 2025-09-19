from __future__ import annotations

import json

from fastapi import APIRouter, Depends, UploadFile, File, Form, HTTPException
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import MessageReferenceDto
from ._messages_common import PostBody, fetch_messages, save_message
from ...db.models import ChannelKind

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
    return await save_message(body, ctx, db, channel_kind=ChannelKind.OFFICER_CHAT)


@router.post("/channels/{channel_id}/officer-messages")
async def post_officer_message_with_attachments(
    channel_id: str,
    content: str = Form(...),
    useCharacterName: bool | None = Form(False),
    message_reference: str | None = Form(None),
    files: list[UploadFile] | None = File(None),
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    ref = None
    if message_reference:
        try:
            ref = MessageReferenceDto(**json.loads(message_reference))
        except Exception:
            raise HTTPException(status_code=400, detail="invalid message_reference")

    body = PostBody(
        channel_id=channel_id,
        content=content,
        use_character_name=useCharacterName,
        message_reference=ref,
    )

    return await save_message(
        body,
        ctx,
        db,
        channel_kind=ChannelKind.OFFICER_CHAT,
        files=files,
    )
