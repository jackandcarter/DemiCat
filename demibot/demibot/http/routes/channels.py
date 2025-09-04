from __future__ import annotations

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse as FastAPIJSONResponse
import json
import logging
from sqlalchemy import select, update
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import GuildChannel, ChannelKind
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
    # Include human-friendly channel names so the plugin can display readable
    # labels in dropdowns.
    plugin_kinds = [k for k in ChannelKind if k != ChannelKind.CHAT]
    by_kind: dict[str, list[dict[str, str]]] = {
        kind.value: [] for kind in plugin_kinds
    }
    updated = False
    for kind, channel_id, name in result.all():
        if kind not in plugin_kinds:
            continue
        new_name = await ensure_channel_name(db, ctx.guild.id, channel_id, kind, name)
        if new_name is None:
            logging.warning(
                "Channel name missing for %s (%s) in guild %s",
                channel_id,
                kind.value,
                ctx.guild.id,
            )
            by_kind[kind.value].append({"id": str(channel_id), "name": str(channel_id)})
            if name is not None:
                await db.execute(
                    update(GuildChannel)
                    .where(
                        GuildChannel.guild_id == ctx.guild.id,
                        GuildChannel.channel_id == channel_id,
                        GuildChannel.kind == kind,
                    )
                    .values(name=None)
                )
                updated = True
            continue
        if new_name != name:
            name = new_name
            updated = True
        by_kind[kind.value].append({"id": str(channel_id), "name": name})
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
    plugin_kinds = [k for k in ChannelKind if k != ChannelKind.CHAT]
    updated = False
    for kind, channel_id, name in result.all():
        if kind not in plugin_kinds:
            continue
        new_name = await ensure_channel_name(db, ctx.guild.id, channel_id, kind, name)
        if new_name is None:
            logging.warning(
                "Channel name missing for %s (%s) in guild %s",
                channel_id,
                kind.value,
                ctx.guild.id,
            )
            if name is not None:
                await db.execute(
                    update(GuildChannel)
                    .where(
                        GuildChannel.guild_id == ctx.guild.id,
                        GuildChannel.channel_id == channel_id,
                        GuildChannel.kind == kind,
                    )
                    .values(name=None)
                )
                updated = True
            continue
        if new_name != name:
            updated = True
    if updated:
        await db.commit()
        await manager.broadcast_text("update", ctx.guild.id, path="/ws/channels")
    return {"status": "ok"}
