from __future__ import annotations

import json

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import Embed

router = APIRouter(prefix="/api")


@router.get("/embeds")
async def get_embeds(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    result = await db.execute(
        select(Embed).where(Embed.guild_id == ctx.guild.id)
    )
    return [json.loads(e.payload_json) for e in result.scalars()]
