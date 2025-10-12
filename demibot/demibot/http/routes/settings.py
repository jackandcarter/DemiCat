import os

from fastapi import APIRouter

router = APIRouter(prefix="/api")

@router.get("/settings")
async def get_settings():
    fc_enabled = os.getenv("FEATURE_FC_SYNCSHELL", "false").lower() in {
        "1",
        "true",
        "yes",
        "on",
    }
    return {
        "syncedChat": True,
        "events": True,
        "templates": True,
        "requests": True,
        "officer": True,
        "enableFcChat": True,
        "fcSyncShell": fc_enabled,
    }
