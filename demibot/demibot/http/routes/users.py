from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import and_, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import (
    Membership,
    MembershipRole,
    Presence as DbPresence,
    Role,
    User,
)
from ...discordbot.presence_store import get_presences
from ..discord_client import discord_client

router = APIRouter(prefix="/api")


@router.get("/users")
async def get_users(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    stmt = (
        select(User, DbPresence.status, Role.discord_role_id)
        .join(Membership, Membership.user_id == User.id)
        .join(
            DbPresence,
            and_(
                DbPresence.guild_id == Membership.guild_id,
                DbPresence.user_id == User.discord_user_id,
            ),
            isouter=True,
        )
        .join(
            MembershipRole,
            MembershipRole.membership_id == Membership.id,
            isouter=True,
        )
        .join(Role, MembershipRole.role_id == Role.id, isouter=True)
        .where(Membership.guild_id == ctx.guild.id)
    )
    result = await db.execute(stmt)
    rows = result.all()
    cache = {p.id: p.status for p in get_presences(ctx.guild.id)}

    user_map: dict[int, dict[str, object]] = {}
    for u, s, rid in rows:
        entry = user_map.setdefault(
            u.discord_user_id, {"user": u, "status": s, "roles": set()}
        )
        if entry["status"] is None and s is not None:
            entry["status"] = s
        if rid is not None:
            entry["roles"].add(rid)

    avatars: dict[int, str] = {}
    if discord_client:
        guild = discord_client.get_guild(ctx.guild.discord_guild_id)
        for data in user_map.values():
            u = data["user"]  # type: ignore[assignment]
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

    users: list[dict[str, str | list[str] | None]] = []
    for data in user_map.values():
        u = data["user"]  # type: ignore[assignment]
        s = data["status"]
        roles = [str(r) for r in sorted(data["roles"])]
        # Default to the cached presence if the database value is missing.
        status = s or cache.get(u.discord_user_id)
        # Anything that is not explicitly offline counts as online.
        status = "offline" if status in (None, "offline") else "online"
        users.append(
            {
                "id": str(u.discord_user_id),
                "name": u.global_name or str(u.discord_user_id),
                "status": status,
                "avatar_url": avatars.get(u.discord_user_id),
                "roles": roles,
            }
        )
    return users
