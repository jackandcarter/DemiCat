import asyncio
import logging
from typing import Any, Awaitable, Callable, Optional, TypeVar

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
