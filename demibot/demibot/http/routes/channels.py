from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import GuildChannel

router = APIRouter(prefix="/api")


@router.get("/channels")
async def get_channels(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    result = await db.execute(
        select(GuildChannel.kind, GuildChannel.channel_id).where(
            GuildChannel.guild_id == ctx.guild.id
        )
    )
    by_kind: dict[str, list[str]] = {
        "event": [],
        "fc_chat": [],
        "officer_chat": [],
        "officer_visible": [],
    }
    for kind, channel_id in result.all():
        by_kind.setdefault(kind, []).append(str(channel_id))
    return by_kind
