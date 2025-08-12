import hashlib
import json
from fastapi import APIRouter, Depends, Request, Response

from ..api import get_api_key_info, bot

router = APIRouter(prefix="/api/embeds")


@router.get("/")
async def get_embeds(request: Request, info: dict = Depends(get_api_key_info)):
    data = bot.embed_cache
    json_data = json.dumps(data)
    etag = 'W/"' + hashlib.sha1(json_data.encode()).hexdigest() + '"'
    if request.headers.get("if-none-match") == etag:
        return Response(status_code=304)
    return Response(content=json_data, media_type="application/json", headers={"ETag": etag})
