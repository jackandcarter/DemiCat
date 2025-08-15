from __future__ import annotations

from dataclasses import dataclass

from fastapi import Depends, Header, HTTPException, status
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..db.models import Guild, User, UserKey
from ..db.session import get_session


@dataclass
class RequestContext:
    user: User
    guild: Guild
    key: UserKey


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
    return RequestContext(user=user, guild=guild, key=key)
