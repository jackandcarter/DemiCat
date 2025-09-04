from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import and_, select

from ..deps import RequestContext, api_key_auth
from ...db.models import (
    Membership,
    MembershipRole,
    Presence as DbPresence,
    Role,
    User,
)
from ...db.session import get_session
from ...discordbot.presence_store import get_presences
from ..discord_client import discord_client


router = APIRouter(prefix="/api")


@router.get("/presences")
async def list_presences(
    ctx: RequestContext = Depends(api_key_auth),
) -> list[dict[str, str | list[str] | None]]:
    db_presences: list[dict[str, str | list[str] | None]] | None = None
    try:
        async with get_session() as db:
            result = await db.execute(
                select(DbPresence, User, Role.discord_role_id)
                .join(User, User.discord_user_id == DbPresence.user_id, isouter=True)
                .join(
                    Membership,
                    and_(
                        Membership.guild_id == DbPresence.guild_id,
                        Membership.user_id == User.id,
                    ),
                    isouter=True,
                )
                .join(
                    MembershipRole,
                    MembershipRole.membership_id == Membership.id,
                    isouter=True,
                )
                .join(Role, MembershipRole.role_id == Role.id, isouter=True)
                .where(DbPresence.guild_id == ctx.guild.id)
            )
            rows = result.all()
            if rows:
                avatars: dict[int, str] = {}
                if discord_client:
                    guild = discord_client.get_guild(getattr(ctx.guild, "discord_guild_id", ctx.guild.id))
                    for p, _, _ in rows:
                        member = guild.get_member(p.user_id) if guild else None
                        if member and member.display_avatar:
                            avatars[p.user_id] = str(member.display_avatar.url)
                user_map: dict[int, dict[str, object]] = {}
                for p, u, rid in rows:
                    entry = user_map.setdefault(
                        p.user_id, {"presence": p, "user": u, "roles": set()}
                    )
                    if rid is not None:
                        entry["roles"].add(rid)
                db_presences = []
                for data in user_map.values():
                    p = data["presence"]  # type: ignore[assignment]
                    u = data["user"]  # type: ignore[assignment]
                    roles = [str(r) for r in sorted(data["roles"])]
                    db_presences.append(
                        {
                            "id": str(p.user_id),
                            "name": (u.global_name or u.discriminator or str(p.user_id)) if u else str(p.user_id),
                            "status": p.status,
                            "avatar_url": avatars.get(p.user_id),
                            "roles": roles,
                        }
                    )
    except RuntimeError:
        pass
    if db_presences is not None:
        return db_presences
    return [
        {
            "id": str(p.id),
            "name": p.name,
            "status": p.status,
            "avatar_url": p.avatar_url,
            "roles": [str(r) for r in p.roles],
        }
        for p in get_presences(ctx.guild.id)
    ]
