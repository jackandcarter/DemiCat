from __future__ import annotations

import logging
from typing import Dict, List

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse

from ..deps import RequestContext, api_key_auth
from ..discord_client import discord_client, is_discord_client_ready

router = APIRouter(prefix="/api/discord", tags=["discord"])

logger = logging.getLogger(__name__)

_emoji_cache: Dict[int, List[dict]] = {}
_RETRY_AFTER_SECONDS = 2


def _retry_response() -> JSONResponse:
    return JSONResponse(
        {"ok": True, "emojis": []},
        headers={"Retry-After": str(_RETRY_AFTER_SECONDS)},
    )


def _serialize_emojis(emojis) -> List[dict]:
    serialized: List[dict] = []
    for emoji in emojis or []:
        serialized.append(
            {
                "id": str(emoji.id),
                "name": emoji.name,
                "animated": bool(getattr(emoji, "animated", False)),
            }
        )
    return serialized


@router.get("/emojis")
async def list_emojis(ctx: RequestContext = Depends(api_key_auth)):
    cached = _emoji_cache.get(ctx.guild.id)
    if cached is not None:
        return {"ok": True, "emojis": cached}

    client = discord_client
    if client is None or not is_discord_client_ready(client):
        return _retry_response()

    try:
        guild = client.get_guild(ctx.guild.discord_guild_id)
    except Exception:  # pragma: no cover - defensive logging
        logger.exception(
            "Failed to look up guild %s", ctx.guild.discord_guild_id
        )
        return _retry_response()

    if not guild:
        return _retry_response()

    try:
        emojis = _serialize_emojis(getattr(guild, "emojis", []))
    except Exception:  # pragma: no cover - defensive logging
        logger.exception(
            "Failed to serialize emojis for guild %s", ctx.guild.discord_guild_id
        )
        return _retry_response()

    if not emojis:
        return _retry_response()

    _emoji_cache[ctx.guild.id] = emojis
    return {"ok": True, "emojis": emojis}
