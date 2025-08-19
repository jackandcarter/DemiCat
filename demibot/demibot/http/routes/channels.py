from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import select, update
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..discord_client import discord_client
from ...db.models import GuildChannel

router = APIRouter(prefix="/api")


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
        if not name and discord_client:
            channel = discord_client.get_channel(channel_id)
            if channel is None:
                try:
                    channel = await discord_client.fetch_channel(channel_id)  # type: ignore[attr-defined]
                except Exception:  # pragma: no cover - network errors
                    channel = None
            if channel is not None:
                name = channel.name
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
    return by_kind
