from __future__ import annotations

import json
from typing import Any

from fastapi import APIRouter, Depends
from pydantic import BaseModel
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import SignupPreset

router = APIRouter(prefix="/api")


class SignupPresetBody(BaseModel):
    name: str
    buttons: list[dict[str, Any]]


@router.get("/signup-presets")
async def list_signup_presets(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> list[dict[str, Any]]:
    result = await db.execute(
        select(SignupPreset).where(SignupPreset.guild_id == ctx.guild.id)
    )
    presets: list[dict[str, Any]] = []
    for p in result.scalars():
        try:
            buttons = json.loads(p.buttons_json)
        except Exception:
            buttons = []
        presets.append({"id": str(p.id), "name": p.name, "buttons": buttons})
    return presets


@router.post("/signup-presets")
async def create_signup_preset(
    body: SignupPresetBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    preset = SignupPreset(
        guild_id=ctx.guild.id,
        name=body.name,
        buttons_json=json.dumps(body.buttons),
    )
    db.add(preset)
    await db.commit()
    await db.refresh(preset)
    return {"id": str(preset.id)}


@router.delete("/signup-presets/{preset_id}")
async def delete_signup_preset(
    preset_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    pid = int(preset_id)
    preset = await db.get(SignupPreset, pid)
    if preset and preset.guild_id == ctx.guild.id:
        await db.delete(preset)
        await db.commit()
    return {"ok": True}
