import hashlib
import json
from fastapi import APIRouter, Depends, HTTPException, Request, Response

from ..api import get_api_key_info, db, bot
from python_demibot.rate_limiter import enqueue

router = APIRouter(prefix="/api/officer-messages")


@router.get("/{channel_id}")
async def get_messages(channel_id: str, request: Request, info: dict = Depends(get_api_key_info)):
    try:
        channel = await bot.get_client().fetch_channel(channel_id)
        if not channel or channel.guild.id != int(info["serverId"]):
            raise HTTPException(status_code=403, detail="Forbidden")
        arr = bot.message_cache.get(channel_id, [])
        json_data = json.dumps(arr)
        etag = 'W/"' + hashlib.sha1(json_data.encode()).hexdigest() + '"'
        if request.headers.get("if-none-match") == etag:
            return Response(status_code=304)
        return Response(content=json_data, media_type="application/json", headers={"ETag": etag})
    except HTTPException:
        raise
    except Exception as err:  # pragma: no cover - defensive
        raise HTTPException(status_code=500, detail="Forbidden") from err


@router.post("/")
async def post_message(payload: dict, info: dict = Depends(get_api_key_info)):
    channel_id = payload.get("channelId")
    content = payload.get("content")
    use_character = payload.get("useCharacterName")
    try:
        channel = await bot.get_client().fetch_channel(channel_id)
        if not channel or not channel.is_text_based() or channel.guild.id != int(info["serverId"]):
            raise HTTPException(status_code=400, detail="Invalid channel")
        user = await bot.get_client().fetch_user(int(info["userId"]))
        display_name = info.get("characterName") if use_character and info.get("characterName") else user.username
        hooks = await channel.webhooks()
        hook = next((h for h in hooks if h.name == "DemiCat"), None)
        if not hook:
            hook = await channel.create_webhook(name="DemiCat")
        await enqueue(lambda: hook.send(content, username=display_name))
        await db.set_server_settings(info["serverId"], {"officerChatChannel": channel_id})
        bot.track_officer_channel(channel_id)
        return {"ok": True}
    except HTTPException:
        raise
    except Exception as err:  # pragma: no cover - defensive
        raise HTTPException(status_code=500, detail={"ok": False}) from err
