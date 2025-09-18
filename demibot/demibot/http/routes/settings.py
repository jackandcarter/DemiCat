from fastapi import APIRouter

router = APIRouter(prefix="/api")

@router.get("/settings")
async def get_settings():
    return {
        "syncedChat": True,
        "events": True,
        "templates": True,
        "requests": True,
        "officer": True,
        "enableFcChat": True,
        "fcSyncShell": False,
    }
