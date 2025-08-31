from __future__ import annotations

from fastapi import APIRouter, Depends, UploadFile, File, Form, HTTPException
from sqlalchemy.ext.asyncio import AsyncSession
import json
import discord

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import ChatMessage
from ._messages_common import (
    PostBody,
    fetch_messages,
    save_message,
    edit_message as edit_message_common,
    delete_message as delete_message_common,
)
from ..discord_client import discord_client

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


@router.post("/channels/{channel_id}/messages")
async def post_message_with_attachments(
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
            ref = json.loads(message_reference)
        except Exception:
            raise HTTPException(status_code=400, detail="invalid message_reference")
    body = PostBody(
        channelId=channel_id,
        content=content,
        useCharacterName=useCharacterName,
        messageReference=ref,
    )
    return await save_message(body, ctx, db, is_officer=False, files=files)


@router.put("/channels/{channel_id}/messages/{message_id}/reactions/{emoji}")
async def add_reaction(
    channel_id: str,
    message_id: str,
    emoji: str,
    ctx: RequestContext = Depends(api_key_auth),
):
    if not discord_client:
        raise HTTPException(status_code=503, detail="Discord client unavailable")
    channel = discord_client.get_channel(int(channel_id))
    if not channel or not isinstance(channel, discord.abc.Messageable):
        raise HTTPException(status_code=404)
    try:
        msg = await channel.fetch_message(int(message_id))
        await msg.add_reaction(emoji)
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))
    return {"ok": True}


@router.delete("/channels/{channel_id}/messages/{message_id}/reactions/{emoji}")
async def remove_reaction(
    channel_id: str,
    message_id: str,
    emoji: str,
    ctx: RequestContext = Depends(api_key_auth),
):
    if not discord_client:
        raise HTTPException(status_code=503, detail="Discord client unavailable")
    channel = discord_client.get_channel(int(channel_id))
    if not channel or not isinstance(channel, discord.abc.Messageable):
        raise HTTPException(status_code=404)
    try:
        msg = await channel.fetch_message(int(message_id))
        await msg.remove_reaction(emoji, discord_client.user)
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))
    return {"ok": True}


@router.patch("/channels/{channel_id}/messages/{message_id}")
async def patch_message(
    channel_id: str,
    message_id: str,
    body: dict,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    content = body.get("content")
    if content is None:
        raise HTTPException(status_code=400, detail="content required")
    return await edit_message_common(channel_id, message_id, content, ctx, db, is_officer=False)


@router.delete("/channels/{channel_id}/messages/{message_id}")
async def delete_message(
    channel_id: str,
    message_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    return await delete_message_common(channel_id, message_id, ctx, db, is_officer=False)


@router.post("/channels/{channel_id}/commands")
async def execute_command(
    channel_id: str,
    body: dict,
    ctx: RequestContext = Depends(api_key_auth),
):
    if not discord_client:
        raise HTTPException(status_code=503, detail="Discord client unavailable")
    command = body.get("command")
    if not command:
        raise HTTPException(status_code=400, detail="command required")
    channel = discord_client.get_channel(int(channel_id))
    if not channel or not isinstance(channel, discord.abc.Messageable):
        raise HTTPException(status_code=404)
    try:
        sent = await channel.send(command)
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))
    return {"ok": True, "id": str(sent.id)}
