from fastapi import APIRouter, Depends, HTTPException
from ..deps import RequestContext, api_key_auth
from ..discord_client import discord_client

router = APIRouter(prefix="/api/discord", tags=["discord"])

@router.get("/emojis")
async def list_emojis(ctx: RequestContext = Depends(api_key_auth)):
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
