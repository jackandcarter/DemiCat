from __future__ import annotations

from fastapi import APIRouter, Depends
from pydantic import BaseModel

from ..deps import RequestContext, api_key_auth

router = APIRouter(prefix="/api")


class InteractionRequest(BaseModel):
    MessageId: str
    ChannelId: int
    CustomId: str


@router.post("/interactions")
async def interactions(
    body: InteractionRequest,
    ctx: RequestContext = Depends(api_key_auth),
) -> dict:
    # Placeholder: update attendance and broadcast
    return {"status": "ok"}
