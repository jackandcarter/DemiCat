
from __future__ import annotations
from fastapi import APIRouter, Header, HTTPException
from pydantic import BaseModel
from ...config import AppConfig

router = APIRouter()

cfg = AppConfig()

class KeyBody(BaseModel):
    key: str

class RolesResponse(BaseModel):
    roles: list[str] = []

@router.post("/validate")
async def validate(body: KeyBody, x_api_key: str | None = Header(default=None)):
    key = body.key or (x_api_key or "")
    if key != cfg.security.api_key:
        raise HTTPException(status_code=401, detail="Invalid key")
    return {"ok": True}

@router.post("/roles", response_model=RolesResponse)
async def roles(x_api_key: str | None = Header(default=None)) -> RolesResponse:
    if (x_api_key or "") != cfg.security.api_key:
        raise HTTPException(status_code=401)
    # For now, grant both roles when a valid key is used; customize as needed.
    return RolesResponse(roles=["officer", "chat"])
