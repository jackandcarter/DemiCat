from __future__ import annotations

import json
from typing import Any

from fastapi import APIRouter, Depends, HTTPException
import logging
import discord
from pydantic import BaseModel
from sqlalchemy import select, update
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..ws import manager
from ..discord_client import discord_client
from ...db.models import Request as DbRequest, RequestStatus, RequestType, Urgency, User
from ...config import load_config

router = APIRouter(prefix="/api")


class RequestCreateBody(BaseModel):
    title: str
    description: str | None = None
    type: RequestType
    urgency: Urgency


class RequestPatchBody(BaseModel):
    title: str | None = None
    description: str | None = None
    urgency: Urgency | None = None


class CommentBody(BaseModel):
    text: str


class RequestDto(BaseModel):
    id: str
    title: str
    description: str | None
    type: RequestType
    status: RequestStatus
    urgency: Urgency

    class Config:
        from_attributes = True


def _dto(req: DbRequest) -> dict[str, Any]:
    return RequestDto.model_validate(req).model_dump(mode="json")


async def _broadcast(guild_id: int, request_id: int, delta: dict[str, Any]) -> None:
    payload = json.dumps({"topic": "requests.stream", "payload": delta})
    await manager.broadcast_text(payload, guild_id, path="/ws/requests")
    payload = json.dumps({"topic": f"request.{request_id}", "payload": delta})
    await manager.broadcast_text(payload, guild_id, path="/ws/requests")


async def _requests_channel(guild_id: int) -> discord.abc.Messageable | None:
    if not discord_client:
        return None
    guild = discord_client.get_guild(guild_id)
    if not guild:
        return None
    for channel in guild.text_channels:
        if channel.name == "requests":
            return channel
    return None


async def _send_dm(discord_id: int, message: str) -> None:
    if not discord_client:
        return
    try:
        user = await discord_client.fetch_user(discord_id)
        await user.send(message)
    except Exception:  # pragma: no cover - network errors
        logging.warning("Failed to send DM to %s", discord_id)


async def _notify(
    guild_id: int,
    req: DbRequest,
    action: str,
    db: AsyncSession,
    ctx_user: User,
) -> None:
    channel = await _requests_channel(guild_id)
    if channel:
        cfg = load_config()
        url = f"http://{cfg.server.host}:{cfg.server.port}/board/requests/{req.id}"
        embed = discord.Embed(title=req.title, url=url, description=req.description or "")
        embed.add_field(name="Status", value=req.status.value, inline=False)
        try:
            await channel.send(embed=embed)
        except Exception:  # pragma: no cover - network errors
            logging.warning("Failed to post request embed for %s", req.id)
    requester = await db.get(User, req.user_id)
    if requester:
        await _send_dm(
            requester.discord_user_id,
            f"Your request '{req.title}' was {action}.",
        )
    if ctx_user.id != req.user_id:
        await _send_dm(
            ctx_user.discord_user_id,
            f"You {action} the request '{req.title}'.",
        )


@router.get("/requests")
async def list_requests(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> list[dict[str, Any]]:
    result = await db.execute(select(DbRequest).where(DbRequest.guild_id == ctx.guild.id))
    return [_dto(r) for r in result.scalars()]


@router.post("/requests")
async def create_request(
    body: RequestCreateBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, str]:
    req = DbRequest(
        guild_id=ctx.guild.id,
        user_id=ctx.user.id,
        title=body.title,
        description=body.description,
        type=body.type,
        status=RequestStatus.OPEN,
        urgency=body.urgency,
    )
    db.add(req)
    await db.commit()
    await db.refresh(req)
    await _broadcast(ctx.guild.id, req.id, _dto(req))
    await _notify(ctx.guild.id, req, "created", db, ctx.user)
    return {"id": str(req.id)}


@router.get("/requests/{request_id}")
async def get_request(
    request_id: int,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    req = await db.get(DbRequest, request_id)
    if not req or req.guild_id != ctx.guild.id:
        raise HTTPException(status_code=404)
    return _dto(req)


@router.patch("/requests/{request_id}")
async def update_request(
    request_id: int,
    body: RequestPatchBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, bool]:
    req = await db.get(DbRequest, request_id)
    if not req or req.guild_id != ctx.guild.id:
        raise HTTPException(status_code=404)
    if body.title is not None:
        req.title = body.title
    if body.description is not None:
        req.description = body.description
    if body.urgency is not None:
        req.urgency = body.urgency
    await db.commit()
    await db.refresh(req)
    await _broadcast(ctx.guild.id, req.id, _dto(req))
    return {"ok": True}


@router.delete("/requests/{request_id}")
async def delete_request(
    request_id: int,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, bool]:
    req = await db.get(DbRequest, request_id)
    if not req or req.guild_id != ctx.guild.id:
        raise HTTPException(status_code=404)
    await db.delete(req)
    await db.commit()
    delta = {"id": str(request_id), "deleted": True}
    await _broadcast(ctx.guild.id, request_id, delta)
    return {"ok": True}


async def _update_status(
    db: AsyncSession,
    guild_id: int,
    request_id: int,
    from_status: RequestStatus,
    to_status: RequestStatus,
) -> DbRequest:
    result = await db.execute(
        update(DbRequest)
        .where(
            DbRequest.id == request_id,
            DbRequest.guild_id == guild_id,
            DbRequest.status == from_status,
        )
        .values(status=to_status)
    )
    if result.rowcount == 0:
        raise HTTPException(status_code=409)
    await db.commit()
    req = await db.get(DbRequest, request_id)
    return req


@router.post("/requests/{request_id}/accept")
async def accept_request(
    request_id: int,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, bool]:
    req = await _update_status(
        db, ctx.guild.id, request_id, RequestStatus.OPEN, RequestStatus.ACCEPTED
    )
    await _broadcast(ctx.guild.id, req.id, {"id": str(req.id), "status": req.status})
    await _notify(ctx.guild.id, req, "accepted", db, ctx.user)
    return {"ok": True}


@router.post("/requests/{request_id}/start")
async def start_request(
    request_id: int,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, bool]:
    req = await _update_status(
        db, ctx.guild.id, request_id, RequestStatus.ACCEPTED, RequestStatus.STARTED
    )
    await _broadcast(ctx.guild.id, req.id, {"id": str(req.id), "status": req.status})
    return {"ok": True}


@router.post("/requests/{request_id}/complete")
async def complete_request(
    request_id: int,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, bool]:
    req = await _update_status(
        db, ctx.guild.id, request_id, RequestStatus.STARTED, RequestStatus.COMPLETED
    )
    await _broadcast(ctx.guild.id, req.id, {"id": str(req.id), "status": req.status})
    await _notify(ctx.guild.id, req, "completed", db, ctx.user)
    return {"ok": True}


@router.post("/requests/{request_id}/confirm")
async def confirm_request(
    request_id: int,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, bool]:
    req = await _update_status(
        db, ctx.guild.id, request_id, RequestStatus.COMPLETED, RequestStatus.CONFIRMED
    )
    await _broadcast(ctx.guild.id, req.id, {"id": str(req.id), "status": req.status})
    return {"ok": True}


@router.post("/requests/{request_id}/cancel")
async def cancel_request(
    request_id: int,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, bool]:
    result = await db.execute(
        update(DbRequest)
        .where(
            DbRequest.id == request_id,
            DbRequest.guild_id == ctx.guild.id,
            DbRequest.status != RequestStatus.CONFIRMED,
            DbRequest.status != RequestStatus.CANCELLED,
        )
        .values(status=RequestStatus.CANCELLED)
    )
    if result.rowcount == 0:
        raise HTTPException(status_code=409)
    await db.commit()
    req = await db.get(DbRequest, request_id)
    await _broadcast(ctx.guild.id, req.id, {"id": str(req.id), "status": req.status})
    return {"ok": True}


@router.post("/requests/{request_id}/comment")
async def comment_request(
    request_id: int,
    body: CommentBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, bool]:
    req = await db.get(DbRequest, request_id)
    if not req or req.guild_id != ctx.guild.id:
        raise HTTPException(status_code=404)
    delta = {"id": str(req.id), "comment": body.text}
    await _broadcast(ctx.guild.id, req.id, delta)
    return {"ok": True}

