from __future__ import annotations

import json

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import Embed, GuildChannel

router = APIRouter(prefix="/api")


@router.get("/embeds")
async def get_embeds(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    stmt = select(Embed).where(Embed.guild_id == ctx.guild.id)
    if "officer" not in ctx.roles:
        stmt = stmt.join(
            GuildChannel, GuildChannel.channel_id == Embed.channel_id
        ).where(GuildChannel.kind != "officer_chat")
    stmt = stmt.order_by(Embed.updated_at.desc())
    result = await db.execute(stmt)
    embeds = []
    for e in result.scalars().all():
        payload = json.loads(e.payload_json)
        if not payload.get("channelId"):
            payload["channelId"] = e.channel_id
        payload["guildId"] = e.guild_id
        embeds.append(payload)
    return embeds
