from __future__ import annotations

from time import monotonic
from typing import Dict, List, Tuple
import json
from pathlib import Path

from fastapi import APIRouter, Depends

from ..deps import RequestContext, api_key_auth
from ..discord_client import discord_client

router = APIRouter(prefix="/api")

_CACHE_TTL = 600  # seconds
_emoji_cache: Dict[int, Tuple[List[dict], float]] = {}
_unicode_cache: Tuple[List[dict], float] | None = None


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


@router.get("/emojis/guilds/{guild_id}")
async def get_guild_emojis(
    guild_id: int, ctx: RequestContext = Depends(api_key_auth)
) -> List[dict]:
    cache_entry = _emoji_cache.get(guild_id)
    if cache_entry:
        data, ts = cache_entry
        if monotonic() - ts < _CACHE_TTL:
            return data
    if discord_client:
        guild = discord_client.get_guild(guild_id)
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
            _emoji_cache[guild_id] = (data, monotonic())
            return data
    return []


@router.get("/emojis/unicode")
async def get_unicode_emojis() -> List[dict]:
    global _unicode_cache
    if _unicode_cache:
        data, ts = _unicode_cache
        if monotonic() - ts < _CACHE_TTL:
            return data
    data_path = Path(__file__).resolve().parents[1] / "data" / "unicode_emojis.json"
    try:
        with data_path.open("r", encoding="utf-8") as f:
            data = json.load(f)
    except Exception:
        data = []
    _unicode_cache = (data, monotonic())
    return data
