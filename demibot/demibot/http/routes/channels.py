from __future__ import annotations

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse as FastAPIJSONResponse
import json
from sqlalchemy import select, update
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import GuildChannel
from ...channel_names import ensure_channel_name
from ..ws import manager

router = APIRouter(prefix="/api")


class JSONResponse(FastAPIJSONResponse):
    def __init__(self, content, *args, ensure_ascii: bool = False, **kwargs):
        self.ensure_ascii = ensure_ascii
        super().__init__(content, *args, **kwargs)

    def render(self, content) -> bytes:
        return json.dumps(
            content,
            ensure_ascii=self.ensure_ascii,
            allow_nan=False,
            indent=None,
            separators=(",", ":"),
        ).encode("utf-8")


@router.get("/channels")
async def get_channels(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    result = await db.execute(
        select(GuildChannel.kind, GuildChannel.channel_id, GuildChannel.name).where(
            GuildChannel.guild_id == ctx.guild.id
        )
    )
    by_kind: dict[str, list[dict[str, str]]] = {
        "event": [],
        "fc_chat": [],
        "officer_chat": [],
        "officer_visible": [],
    }
    updated = False
    for kind, channel_id, name in result.all():
        new_name = await ensure_channel_name(db, ctx.guild.id, channel_id, kind, name)
        if new_name is not None and new_name != name:
            name = new_name
            await db.execute(
                update(GuildChannel)
                .where(
                    GuildChannel.guild_id == ctx.guild.id,
                    GuildChannel.channel_id == channel_id,
                    GuildChannel.kind == kind,
                )
                .values(name=name)
            )
            updated = True
        by_kind.setdefault(kind, []).append({"id": str(channel_id), "name": name or ""})
    if updated:
        await db.commit()
        await manager.broadcast_text("update", ctx.guild.id, path="/ws/channels")
    return JSONResponse(content=by_kind, ensure_ascii=False)


@router.post("/channels/refresh")
async def refresh_channels(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    result = await db.execute(
        select(GuildChannel.kind, GuildChannel.channel_id, GuildChannel.name).where(
            GuildChannel.guild_id == ctx.guild.id
        )
    )
    updated = False
    for kind, channel_id, name in result.all():
        new_name = await ensure_channel_name(db, ctx.guild.id, channel_id, kind, name)
        if new_name is not None and new_name != name:
            await db.execute(
                update(GuildChannel)
                .where(
                    GuildChannel.guild_id == ctx.guild.id,
                    GuildChannel.channel_id == channel_id,
                    GuildChannel.kind == kind,
                )
                .values(name=new_name)
            )
            updated = True
    if updated:
        await db.commit()
        await manager.broadcast_text("update", ctx.guild.id, path="/ws/channels")
    return {"status": "ok"}
