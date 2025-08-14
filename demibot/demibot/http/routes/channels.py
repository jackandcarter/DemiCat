
from __future__ import annotations
from fastapi import APIRouter
from ._stores import CHANNELS

router = APIRouter(prefix="/api")

@router.get("/channels")
async def get_channels():
    return {
        "event": CHANNELS.get("event", []),
        "fc_chat": CHANNELS.get("fc_chat", []),
        "officer_chat": CHANNELS.get("officer_chat", []),
        "officer_visible": CHANNELS.get("officer_visible", []),
    }
