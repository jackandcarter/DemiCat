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


def _normalize_status(value: str | None) -> str:
    if not value:
        return "offline"
    lowered = value.lower()
    if lowered in {"offline", "invisible"}:
        return "offline"
    if lowered == "idle":
        return "idle"
    if lowered in {"dnd", "do_not_disturb"}:
        return "dnd"
    return "online"


router = APIRouter(prefix="/api")


@router.get("/presences")
async def list_presences(
    ctx: RequestContext = Depends(api_key_auth),
) -> list[dict[str, str | list[str] | None]]:
    db_presences: list[dict[str, object | None]] | None = None
    try:
        async with get_session() as db:
            result = await db.execute(
                select(
                    DbPresence,
                    User,
                    Role.discord_role_id,
                    Role.name,
                )
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
                role_names: dict[int, str] = {}
                guild = None
                if discord_client:
                    guild = discord_client.get_guild(getattr(ctx.guild, "discord_guild_id", ctx.guild.id))
                    if guild:
                        role_names = {
                            r.id: r.name
                            for r in guild.roles
                            if r.name != "@everyone"
                        }
                    for p, _, _, _ in rows:
                        member = guild.get_member(p.user_id) if guild else None
                        if member and member.display_avatar:
                            avatars[p.user_id] = str(member.display_avatar.url)
                user_map: dict[int, dict[str, object]] = {}
                for p, u, rid, role_name in rows:
                    entry = user_map.setdefault(
                        p.user_id,
                        {
                            "presence": p,
                            "user": u,
                            "roles": {},
                        },
                    )
                    if rid is not None:
                        roles_dict = entry["roles"]  # type: ignore[assignment]
                        if role_name is not None:
                            roles_dict[rid] = role_name
                        else:
                            roles_dict.setdefault(rid, None)
                db_presences = []
                for data in user_map.values():
                    p = data["presence"]  # type: ignore[assignment]
                    u = data["user"]  # type: ignore[assignment]
                    raw_status = getattr(p, "status", None)
                    status = _normalize_status(raw_status)
                    role_map: dict[int, str | None] = dict(data["roles"])  # type: ignore[arg-type]
                    role_details = [
                        {
                            "id": str(rid),
                            "name": role_map[rid] or role_names.get(rid) or str(rid),
                        }
                        for rid in role_map
                    ]
                    db_presences.append(
                        {
                            "id": str(p.user_id),
                            "name": (u.global_name or u.discriminator or str(p.user_id)) if u else str(p.user_id),
                            "status": status,
                            "avatar_url": avatars.get(p.user_id),
                            "roles": [str(rid) for rid in role_map],
                            "status_text": getattr(p, "status_text", None),
                            "role_details": role_details,
                        }
                    )
    except RuntimeError:
        pass
    if db_presences is not None:
        return db_presences
    presences = get_presences(ctx.guild.id)
    role_names: dict[int, str] = {}
    if discord_client:
        guild = discord_client.get_guild(getattr(ctx.guild, "discord_guild_id", ctx.guild.id))
        if guild:
            role_names = {
                r.id: r.name
                for r in guild.roles
                if r.name != "@everyone"
            }
    return [
        {
            "id": str(p.id),
            "name": p.name,
            "status": _normalize_status(p.status),
            "avatar_url": p.avatar_url,
            "roles": [str(r) for r in p.roles],
            "status_text": p.status_text,
            "role_details": [
                {
                    "id": str(rid),
                    "name": role_names.get(rid, str(rid)),
                }
                for rid in p.roles
            ],
        }
        for p in presences
    ]
