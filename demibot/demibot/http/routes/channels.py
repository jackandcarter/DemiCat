from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import GuildConfig

router = APIRouter(prefix="/api")


@router.get("/channels")
async def get_channels(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict:
    stmt = select(GuildConfig).where(GuildConfig.guild_id == ctx.guild.id)
    result = await db.execute(stmt)
    cfg = result.scalar_one_or_none()
    return {
        "event": [str(cfg.event_channel_id)] if cfg and cfg.event_channel_id else [],
        "fc_chat": [str(cfg.fc_chat_channel_id)] if cfg and cfg.fc_chat_channel_id else [],
        "officer_chat": [str(cfg.officer_chat_channel_id)] if cfg and cfg.officer_chat_channel_id else [],
        "officer_visible": [str(cfg.officer_visible_channel_id)] if cfg and cfg.officer_visible_channel_id else [],
    }
