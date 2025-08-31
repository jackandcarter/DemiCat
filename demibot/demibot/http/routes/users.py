from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import and_, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import Membership, Presence as DbPresence, User
from ...discordbot.presence_store import get_presences
from ..discord_client import discord_client

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
    avatars: dict[int, str] = {}
    if discord_client:
        guild = discord_client.get_guild(ctx.guild.discord_guild_id)
        for u, _ in rows:
            avatar: str | None = None
            member = guild.get_member(u.discord_user_id) if guild else None
            if member and member.display_avatar:
                avatar = str(member.display_avatar.url)
            if avatar is None:
                try:
                    user_obj = discord_client.get_user(u.discord_user_id) or await discord_client.fetch_user(u.discord_user_id)  # type: ignore[attr-defined]
                except Exception:
                    user_obj = None
                if user_obj and user_obj.display_avatar:
                    avatar = str(user_obj.display_avatar.url)
            if avatar is not None:
                avatars[u.discord_user_id] = avatar
    users: list[dict[str, str | None]] = []
    for u, s in rows:
        status = s or cache.get(u.discord_user_id)
        status = "online" if status == "online" else "offline"
        users.append(
            {
                "id": str(u.discord_user_id),
                "name": u.global_name or str(u.discord_user_id),
                "status": status,
                "avatar_url": avatars.get(u.discord_user_id),
            }
        )
    return users
