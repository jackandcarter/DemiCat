from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import User

router = APIRouter(prefix="/api")


@router.get("/users")
async def get_users(
    _: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    result = await db.execute(select(User))
    return [
        {
            "id": str(u.discord_user_id),
            "name": u.global_name or str(u.discord_user_id),
        }
        for u in result.scalars()
    ]
