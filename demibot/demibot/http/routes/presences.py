from __future__ import annotations

from fastapi import APIRouter, Depends

from ..deps import RequestContext, api_key_auth
from ...discordbot.presence_store import get_presences


router = APIRouter(prefix="/api")


@router.get("/presences")
async def list_presences(ctx: RequestContext = Depends(api_key_auth)) -> list[dict[str, str]]:
    presences = [
        {"id": str(p.id), "name": p.name, "status": p.status}
        for p in get_presences(ctx.guild.id)
    ]
    return presences
