from __future__ import annotations

from dataclasses import dataclass

from fastapi import Depends, Header, HTTPException, status
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
    x_api_key: str = Header(alias="X-Api-Key"),
    db: AsyncSession = Depends(get_db),
) -> RequestContext:
    stmt = (
        select(User, Guild, UserKey)
        .join(UserKey, User.id == UserKey.user_id)
        .join(Guild, Guild.id == UserKey.guild_id)
        .where(UserKey.token == x_api_key, UserKey.enabled)
    )
    result = await db.execute(stmt)
    row = result.one_or_none()
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
    roles: list[str] = []
    for r in roles_result.scalars():
        if r.is_officer:
            roles.append("officer")
        if r.is_chat:
            roles.append("chat")
    return RequestContext(user=user, guild=guild, key=key, roles=roles)
