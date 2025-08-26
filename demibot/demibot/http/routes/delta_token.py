from __future__ import annotations

from datetime import datetime

from fastapi import APIRouter, Depends
from sqlalchemy import update
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import FcUser

router = APIRouter(prefix="/api")


@router.get("/delta-token")
async def get_delta_token(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    now = datetime.utcnow()
    await db.execute(
        update(FcUser).where(FcUser.user_id == ctx.user.id).values(last_pull_at=now)
    )
    await db.commit()
    return {"since": now.isoformat()}
