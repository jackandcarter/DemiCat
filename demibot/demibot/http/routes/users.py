from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import User, Membership

router = APIRouter(prefix="/api")


@router.get("/users")
async def get_users(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    stmt = (
        select(User)
        .join(Membership, Membership.user_id == User.id)
        .where(Membership.guild_id == ctx.guild.id)
    )
    result = await db.execute(stmt)
    return [
        {
            "id": str(u.discord_user_id),
            "name": u.global_name or str(u.discord_user_id),
        }
        for u in result.scalars()
    ]
