from __future__ import annotations

from collections import defaultdict
from typing import Any

from fastapi import APIRouter, Depends, HTTPException, status
from sqlalchemy import and_, func, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import GuildMemberBan, Membership, User, UserKey


router = APIRouter(prefix="/api")


def _require_officer(ctx: RequestContext) -> None:
    if "officer" not in ctx.roles:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN)


@router.get("/officer/members")
async def get_officer_members(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> list[dict[str, Any]]:
    _require_officer(ctx)

    stmt = (
        select(
            User.discord_user_id,
            User.global_name,
            User.character_name,
            Membership.nickname,
            UserKey.last_used_at,
        )
        .join(UserKey, User.id == UserKey.user_id)
        .join(
            Membership,
            and_(
                Membership.guild_id == ctx.guild.id,
                Membership.user_id == User.id,
            ),
            isouter=True,
        )
        .where(UserKey.guild_id == ctx.guild.id, UserKey.enabled)
    )

    result = await db.execute(stmt)
    rows = result.all()
    if not rows:
        return []

    grouped: dict[int, dict[str, Any]] = defaultdict(lambda: {
        "discord_user_id": 0,
        "global_name": None,
        "character_name": None,
        "nickname": None,
        "last_used_at": None,
    })

    for discord_id, global_name, character_name, nickname, last_used_at in rows:
        entry = grouped[discord_id]
        entry["discord_user_id"] = discord_id
        if entry["global_name"] is None and global_name is not None:
            entry["global_name"] = global_name
        if entry["character_name"] is None and character_name is not None:
            entry["character_name"] = character_name
        if entry["nickname"] is None and nickname is not None:
            entry["nickname"] = nickname
        if last_used_at is not None:
            current = entry.get("last_used_at")
            if current is None or last_used_at > current:
                entry["last_used_at"] = last_used_at

    banned_stmt = select(GuildMemberBan.discord_user_id).where(
        GuildMemberBan.guild_id == ctx.guild.id
    )
    banned_result = await db.execute(banned_stmt)
    banned_ids = {row[0] for row in banned_result.all()}

    payload: list[dict[str, Any]] = []
    for entry in grouped.values():
        discord_id = entry["discord_user_id"]
        nickname = entry["nickname"]
        global_name = entry["global_name"]
        character_name = entry["character_name"]

        display_name = nickname or global_name or character_name or str(discord_id)
        last_used_at = entry.get("last_used_at")
        payload.append(
            {
                "discordUserId": str(discord_id),
                "displayName": display_name,
                "nickname": nickname,
                "globalName": global_name,
                "characterName": character_name,
                "lastUsedAt": last_used_at.isoformat() if last_used_at else None,
                "isBanned": discord_id in banned_ids,
            }
        )

    payload.sort(key=lambda item: item["displayName"].casefold())
    return payload


@router.post("/officer/members/{discord_user_id}/remove")
async def remove_member_access(
    discord_user_id: int,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, str]:
    _require_officer(ctx)

    user = await db.scalar(select(User).where(User.discord_user_id == discord_user_id))
    if user is None:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="member_not_found")

    result = await db.execute(
        update(UserKey)
        .where(
            UserKey.user_id == user.id,
            UserKey.guild_id == ctx.guild.id,
            UserKey.enabled,
        )
        .values(enabled=False, updated_at=func.now())
    )

    if result.rowcount == 0:
        has_keys = await db.scalar(
            select(UserKey.id).where(
                UserKey.user_id == user.id,
                UserKey.guild_id == ctx.guild.id,
            )
        )
        if has_keys is None:
            raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="member_not_found")

    await db.commit()
    return {"status": "ok"}


@router.post("/officer/members/{discord_user_id}/ban")
async def ban_member(
    discord_user_id: int,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, str]:
    _require_officer(ctx)

    user = await db.scalar(select(User).where(User.discord_user_id == discord_user_id))
    if user is not None:
        await db.execute(
            update(UserKey)
            .where(
                UserKey.user_id == user.id,
                UserKey.guild_id == ctx.guild.id,
                UserKey.enabled,
            )
            .values(enabled=False, updated_at=func.now())
        )

    existing = await db.scalar(
        select(GuildMemberBan.id).where(
            GuildMemberBan.guild_id == ctx.guild.id,
            GuildMemberBan.discord_user_id == discord_user_id,
        )
    )

    if existing is None:
        ban = GuildMemberBan(guild_id=ctx.guild.id, discord_user_id=discord_user_id)
        db.add(ban)

    await db.commit()
    return {"status": "ok"}
