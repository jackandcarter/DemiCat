from fastapi import APIRouter, Depends, HTTPException

from ..api import get_api_key_info, db

router = APIRouter(prefix="/api/channels")


@router.get("/")
async def get_channels(info: dict = Depends(get_api_key_info)):
    try:
        settings = await db.get_server_settings(info["serverId"])
        event = settings.get("eventChannels", [])
        fc = [settings["fcChatChannel"]] if settings.get("fcChatChannel") else []
        officer = [settings["officerChatChannel"]] if settings.get("officerChatChannel") else []
        return {"event": event, "fc_chat": fc, "officer_chat": officer}
    except Exception as err:  # pragma: no cover - defensive
        raise HTTPException(status_code=500, detail="Failed to fetch channels") from err
