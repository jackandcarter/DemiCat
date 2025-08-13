from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import User, UserKey

router = APIRouter(prefix="/api")


@router.get("/users")
async def list_users(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> list[dict]:
    stmt = (
        select(User)
        .join(UserKey, User.id == UserKey.user_id)
        .where(UserKey.guild_id == ctx.guild.id)
    )
    result = await db.execute(stmt)
    users = result.scalars().all()
    return [{"id": str(u.discord_user_id), "name": u.global_name or "Unknown"} for u in users]
