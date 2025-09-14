from __future__ import annotations

import asyncio

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
        select(User, Membership.nickname, DbPresence.status, Role.discord_role_id)
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
    for u, n, s, rid in rows:
        entry = user_map.setdefault(
            u.discord_user_id,
            {"user": u, "nickname": n, "status": s, "roles": set()},
        )
        if entry["status"] is None and s is not None:
            entry["status"] = s
        if entry["nickname"] is None and n is not None:
            entry["nickname"] = n
        if rid is not None:
            entry["roles"].add(rid)

    avatars: dict[int, str] = {}
    usernames: dict[int, str] = {}
    missing_ids: set[int] = set()
    if discord_client:
        guild = discord_client.get_guild(ctx.guild.discord_guild_id)
        for data in user_map.values():
            u = data["user"]  # type: ignore[assignment]
            avatar: str | None = None
            username: str | None = None

            member = guild.get_member(u.discord_user_id) if guild else None
            if member:
                if getattr(member, "display_avatar", None):
                    avatar = str(member.display_avatar.url)
                username = getattr(member, "name", None)

            user_obj = None
            if avatar is None or username is None:
                try:
                    user_obj = discord_client.get_user(u.discord_user_id)
                except Exception:
                    user_obj = None
            if user_obj and (avatar is None or username is None):
                if avatar is None and getattr(user_obj, "display_avatar", None):
                    avatar = str(user_obj.display_avatar.url)
                if username is None:
                    username = user_obj.name

            if avatar is not None:
                avatars[u.discord_user_id] = avatar
            if username is not None:
                usernames[u.discord_user_id] = username
            if avatar is None or username is None:
                missing_ids.add(u.discord_user_id)

        if missing_ids:
            sem = asyncio.Semaphore(5)

            async def fetch(uid: int):
                try:
                    async with sem:
                        return await discord_client.fetch_user(uid)  # type: ignore[attr-defined]
                except Exception:
                    return None

            fetched_users = await asyncio.gather(*(fetch(uid) for uid in missing_ids))
            for user_obj in fetched_users:
                if not user_obj:
                    continue
                if user_obj.id not in avatars and getattr(user_obj, "display_avatar", None):
                    avatars[user_obj.id] = str(user_obj.display_avatar.url)
                if user_obj.id not in usernames:
                    usernames[user_obj.id] = user_obj.name

    users: list[dict[str, str | list[str] | None]] = []
    for data in user_map.values():
        u = data["user"]  # type: ignore[assignment]
        s = data["status"]
        roles = [str(r) for r in sorted(data["roles"])]
        # Default to the cached presence if the database value is missing.
        status = s or cache.get(u.discord_user_id)
        # Anything that is not explicitly offline counts as online.
        status = "offline" if status in (None, "offline") else "online"
        name = (
            data.get("nickname")
            or u.global_name
            or usernames.get(u.discord_user_id)
            or str(u.discord_user_id)
        )
        users.append(
            {
                "id": str(u.discord_user_id),
                "name": name,
                "status": status,
                "avatar_url": avatars.get(u.discord_user_id),
                "roles": roles,
            }
        )
    return users
