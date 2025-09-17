import asyncio
import logging
from typing import Any, Awaitable, Callable, Optional, TypeVar, cast

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..db.models import Membership, User

try:  # optional dependencies for testing environments
    import aiohttp  # type: ignore
except Exception:  # pragma: no cover
    aiohttp = None  # type: ignore

try:  # pragma: no cover - optional
    import discord  # type: ignore
except Exception:  # pragma: no cover
    discord = None  # type: ignore

T = TypeVar("T")

_logger = logging.getLogger(__name__)

_MISSING = object()


async def api_call_with_retries(
    func: Callable[..., Awaitable[T]],
    *args: Any,
    retries: int = 3,
    base_delay: float = 0.5,
    log: Optional[logging.Logger] = None,
    **kwargs: Any,
) -> T:
    """Execute an async call with exponential backoff.

    Parameters
    ----------
    func:
        Awaitable callable representing the API request.
    retries:
        Number of attempts before giving up. Defaults to 3.
    base_delay:
        Initial delay in seconds before retrying. Each subsequent retry doubles
        this delay.
    log:
        Optional logger to use. Defaults to a module-level logger.
    """

    logger = log or _logger
    attempt = 0
    while True:
        try:
            return await func(*args, **kwargs)
        except Exception as exc:  # broad catch so missing deps don't break
            status = getattr(exc, "status", None)
            is_aiohttp = aiohttp and isinstance(exc, aiohttp.ClientError)
            is_discord = discord and isinstance(exc, getattr(discord, "HTTPException", BaseException))
            retryable = False
            if is_discord or is_aiohttp:
                retryable = status is None or 500 <= int(status) < 600
            elif status is not None:
                retryable = 500 <= int(status) < 600
            if attempt >= retries - 1 or not retryable:
                logger.error(
                    "API call failed",
                    extra={"attempt": attempt + 1, "status": status, "error": str(exc)},
                )
                raise
            logger.warning(
                "API call retry",
                extra={"attempt": attempt + 1, "status": status, "error": str(exc)},
            )
            delay = base_delay * (2 ** attempt)
            attempt += 1
            await asyncio.sleep(delay)


async def get_or_create_user(
    db: AsyncSession,
    *,
    discord_user_id: int,
    global_name: str | None | object = _MISSING,
    discriminator: str | None | object = _MISSING,
    avatar_url: str | None | object = _MISSING,
    guild_id: int | None = None,
) -> User:
    """Ensure a :class:`User` exists for the given Discord user id.

    The returned ORM instance belongs to ``db`` and has up-to-date
    ``global_name`` and ``discriminator`` fields.  When ``avatar_url`` and
    ``guild_id`` are provided any existing membership row will also be updated.
    No commit is issued; callers are expected to manage transactions.
    """

    result = await db.execute(
        select(User).where(User.discord_user_id == discord_user_id)
    )
    user = result.scalar_one_or_none()

    if user is None:
        user = User(
            discord_user_id=discord_user_id,
            global_name=global_name,
            discriminator=discriminator,
        )
        bind = db.get_bind()
        if bind is not None and bind.dialect.name == "sqlite":
            user.id = discord_user_id
        db.add(user)
        await db.flush()
    else:
        if global_name is not _MISSING and user.global_name != global_name:
            user.global_name = cast(Optional[str], global_name)
        if discriminator is not _MISSING and user.discriminator != discriminator:
            user.discriminator = cast(Optional[str], discriminator)

    if guild_id is not None and avatar_url is not _MISSING:
        membership = await db.scalar(
            select(Membership).where(
                Membership.guild_id == guild_id,
                Membership.user_id == user.id,
            )
        )
        if membership and membership.avatar_url != avatar_url:
            membership.avatar_url = cast(Optional[str], avatar_url)

    return user
