from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import select

from ..deps import RequestContext, api_key_auth
from ..schemas import PresenceDto
from ...db.models import Presence as DbPresence, User
from ...db.session import get_session
from ...discordbot.presence_store import get_presences


router = APIRouter(prefix="/api")


@router.get("/presences")
async def list_presences(
    ctx: RequestContext = Depends(api_key_auth),
) -> list[PresenceDto]:
    db_presences: list[PresenceDto] | None = None
    try:
        async for db in get_session():
            result = await db.execute(
                select(DbPresence, User)
                    .join(User, User.discord_user_id == DbPresence.user_id, isouter=True)
                    .where(DbPresence.guild_id == ctx.guild.id)
            )
            rows = result.all()
            if rows:
                db_presences = [
                    PresenceDto(
                        id=str(p.user_id),
                        name=(u.global_name or u.discriminator or str(p.user_id)) if u else str(p.user_id),
                        status=p.status,
                        avatarUrl=u.avatar_url if u else None,
                    )
                    for p, u in rows
                ]
            break
    except RuntimeError:
        pass
    if db_presences is not None:
        return db_presences
    return [
        PresenceDto(id=str(p.id), name=p.name, status=p.status, avatarUrl=p.avatar_url)
        for p in get_presences(ctx.guild.id)
    ]
