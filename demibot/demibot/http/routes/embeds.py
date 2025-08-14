
from __future__ import annotations
from fastapi import APIRouter
from ._stores import EMBEDS

router = APIRouter(prefix="/api")

@router.get("/embeds")
async def get_embeds():
    # return list of EmbedDto payloads
    return [e.payload for e in EMBEDS.values()]
