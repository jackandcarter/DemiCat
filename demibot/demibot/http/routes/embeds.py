from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import EmbedDto
from ...db.models import Embed

router = APIRouter(prefix="/api")


@router.get("/embeds", response_model=list[EmbedDto])
async def get_embeds(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> list[EmbedDto]:
    stmt = select(Embed).where(Embed.guild_id == ctx.guild.id)
    result = await db.execute(stmt)
    embeds = result.scalars().all()
    out: list[EmbedDto] = []
    for e in embeds:
        out.append(EmbedDto(id=str(e.discord_message_id), channelId=e.channel_id))
    return out
