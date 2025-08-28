from __future__ import annotations

from dataclasses import dataclass

import logging
from fastapi import Depends, Header, HTTPException, status, Request
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..db.models import Guild, User, UserKey, Membership, MembershipRole, Role
from ..db.session import get_session


@dataclass
class RequestContext:
    user: User
    guild: Guild
    key: UserKey
    roles: list[str]


async def get_db() -> AsyncSession:
    async for session in get_session():
        yield session


async def api_key_auth(
    request: Request = None,
    x_api_key: str | None = Header(None, alias="X-Api-Key"),
    db: AsyncSession = Depends(get_db),
) -> RequestContext:
    if not x_api_key:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED)
    stmt = (
        select(User, Guild, UserKey)
        .join(UserKey, User.id == UserKey.user_id)
        .join(Guild, Guild.id == UserKey.guild_id)
        .where(UserKey.token == x_api_key, UserKey.enabled)
    )
    result = await db.execute(stmt)
    row = result.one_or_none()
    client_ip = request.client.host if request and request.client else "unknown"
    logging.debug(
        "API key auth client=%s token=%s result=%s",
        client_ip,
        x_api_key,
        "hit" if row else "miss",
    )
    if not row:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED)
    user, guild, key = row
    roles_stmt = (
        select(Role)
        .join(MembershipRole, MembershipRole.role_id == Role.id)
        .join(Membership, MembershipRole.membership_id == Membership.id)
        .where(Membership.guild_id == guild.id, Membership.user_id == user.id)
    )
    roles_result = await db.execute(roles_stmt)
    roles: set[str] = set()
    for r in roles_result.scalars():
        if r.is_officer:
            roles.add("officer")
        if r.is_chat:
            roles.add("chat")
    logging.info(
        "API %s %s guild=%s user=%s",
        request.method if request else "?",
        request.url.path if request else "?",
        guild.discord_guild_id,
        user.discord_user_id,
    )
    return RequestContext(user=user, guild=guild, key=key, roles=list(roles))
