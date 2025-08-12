import hashlib
import json
from fastapi import APIRouter, Depends, Request, Response

from ..api import get_api_key_info, bot

router = APIRouter(prefix="/api/embeds")


@router.get("/")
async def get_embeds(request: Request, info: dict = Depends(get_api_key_info)):
    if hasattr(bot, "embed_cache_by_guild"):
        data = bot.embed_cache_by_guild.get(info["serverId"], [])
    else:
        data = [
            e
            for e in bot.embed_cache
            if e.get("serverId") == info["serverId"]
        ]
    json_data = json.dumps(data)
    etag = 'W/"' + hashlib.sha1(json_data.encode()).hexdigest() + '"'
    if request.headers.get("if-none-match") == etag:
        return Response(status_code=304)
    return Response(content=json_data, media_type="application/json", headers={"ETag": etag})
