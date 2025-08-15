
from __future__ import annotations
from fastapi import APIRouter, Depends
from pydantic import BaseModel
from ..deps import RequestContext, api_key_auth

router = APIRouter()


class RolesResponse(BaseModel):
    roles: list[str] = []


@router.post("/validate")
async def validate(_: RequestContext = Depends(api_key_auth)):
    return {"ok": True}


@router.post("/roles", response_model=RolesResponse)
async def roles(ctx: RequestContext = Depends(api_key_auth)) -> RolesResponse:
    roles = [
        r.strip()
        for r in (ctx.key.roles_cached or "officer,chat").split(",")
        if r.strip()
    ]
    return RolesResponse(roles=roles)
