from __future__ import annotations

from fastapi import APIRouter, Depends
from pydantic import BaseModel

from ..deps import RequestContext, api_key_auth

router = APIRouter(prefix="/api")


class EventRequest(BaseModel):
    channelId: str
    title: str
    time: str
    description: str | None = None


@router.post("/events")
async def create_event(
    body: EventRequest,
    ctx: RequestContext = Depends(api_key_auth),
) -> dict:
    # Placeholder: actual implementation would post to Discord and store
    return {"status": "ok"}
