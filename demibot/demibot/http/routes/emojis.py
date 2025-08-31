from __future__ import annotations

from time import monotonic
from typing import Dict, List, Tuple

from fastapi import APIRouter, Depends

from ..deps import RequestContext, api_key_auth
from ..discord_client import discord_client

router = APIRouter(prefix="/api")

_CACHE_TTL = 600  # seconds
_emoji_cache: Dict[int, Tuple[List[dict], float]] = {}


@router.get("/emojis")
async def get_emojis(ctx: RequestContext = Depends(api_key_auth)) -> List[dict]:
    cache_entry = _emoji_cache.get(ctx.guild.id)
    if cache_entry:
        data, ts = cache_entry
        if monotonic() - ts < _CACHE_TTL:
            return data
    if discord_client:
        guild = discord_client.get_guild(ctx.guild.discord_guild_id)
        if guild is not None:
            data = [
                {
                    "id": str(e.id),
                    "name": e.name,
                    "isAnimated": bool(getattr(e, "animated", False)),
                    "imageUrl": str(e.url),
                }
                for e in guild.emojis
            ]
            _emoji_cache[ctx.guild.id] = (data, monotonic())
            return data
    return []
