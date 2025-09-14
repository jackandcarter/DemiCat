from fastapi import APIRouter, Depends, HTTPException
from ..auth import with_context, RequestContext
from ...ws.client import discord_client

router = APIRouter(prefix="/api/discord", tags=["discord"])

@router.get("/emojis")
async def list_emojis(ctx: RequestContext = Depends(with_context)):
    if not discord_client:
        raise HTTPException(503, "discord not connected")
    guild = discord_client.get_guild(ctx.guild.id)
    if not guild:
        raise HTTPException(404, "guild not found")

    out = []
    for e in guild.emojis:
        out.append({
            "id": str(e.id),
            "name": e.name,
            "animated": bool(getattr(e, "animated", False)),
            "available": bool(getattr(e, "available", True)),
        })
    return {"ok": True, "emojis": out}
