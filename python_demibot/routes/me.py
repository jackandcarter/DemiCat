from fastapi import APIRouter, Depends, HTTPException

from ..api import get_api_key_info, db, bot

router = APIRouter(prefix="/api/me")


@router.get("/")
async def get_me(info: dict = Depends(get_api_key_info)):
    return {"userId": info.get("userId"), "isAdmin": bool(info.get("isAdmin"))}


@router.get("/roles")
async def get_roles(userId: str, info: dict = Depends(get_api_key_info)):
    if userId != info.get("userId"):
        raise HTTPException(status_code=403, detail={"hasOfficerRole": False})
    guild = bot.get_guild(int(info["serverId"]))
    if not guild:
        raise HTTPException(status_code=500, detail={"hasOfficerRole": False})
    try:
        member = await guild.fetch_member(int(userId))
        roles = [str(r.id) for r in member.roles]
        officer_roles = await db.get_officer_roles(info["serverId"])
        has = any(r in officer_roles for r in roles)
        return {"hasOfficerRole": has}
    except Exception as err:  # pragma: no cover - defensive
        raise HTTPException(status_code=500, detail={"hasOfficerRole": False}) from err
