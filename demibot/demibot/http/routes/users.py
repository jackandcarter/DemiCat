from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import and_, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import Membership, Presence as DbPresence, User
from ...discordbot.presence_store import get_presences

router = APIRouter(prefix="/api")


@router.get("/users")
async def get_users(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    stmt = (
        select(User, DbPresence.status)
        .join(Membership, Membership.user_id == User.id)
        .join(
            DbPresence,
            and_(
                DbPresence.guild_id == Membership.guild_id,
                DbPresence.user_id == User.discord_user_id,
            ),
            isouter=True,
        )
        .where(Membership.guild_id == ctx.guild.id)
    )
    result = await db.execute(stmt)
    rows = result.all()
    cache = {p.id: p.status for p in get_presences(ctx.guild.id)}
    return [
        {
            "id": str(u.discord_user_id),
            "name": u.global_name or str(u.discord_user_id),
            "status": s or cache.get(u.discord_user_id, "offline"),
        }
        for u, s in rows
    ]
