from __future__ import annotations

import json
from typing import Any

from datetime import datetime

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel
from sqlalchemy import delete, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import Asset, FcUser, Message, User, UserInstallation

router = APIRouter(prefix="/api")


class SettingsPayload(BaseModel):
    settings: dict[str, Any] | None = None
    consent_sync: bool


@router.get("/users/me/settings")
async def get_my_settings(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    stmt = select(FcUser).where(FcUser.user_id == ctx.user.id)
    result = await db.execute(stmt)
    row = result.scalar_one_or_none()
    if not row:
        raise HTTPException(status_code=404)
    data = json.loads(row.settings) if row.settings else {}
    return {"settings": data, "consent_sync": row.consent_sync}


@router.put("/users/me/settings")
async def put_my_settings(
    payload: SettingsPayload,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    stmt = select(FcUser).where(FcUser.user_id == ctx.user.id)
    result = await db.execute(stmt)
    row = result.scalar_one_or_none()
    if not row:
        raise HTTPException(status_code=404)
    row.settings = json.dumps(payload.settings or {})
    row.consent_sync = payload.consent_sync
    await db.commit()
    return {"settings": payload.settings or {}, "consent_sync": row.consent_sync}


@router.post("/users/me/forget")
async def forget_me(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    result = await db.execute(
        select(UserInstallation.asset_id)
        .join(Asset, UserInstallation.asset_id == Asset.id)
        .where(UserInstallation.user_id == ctx.user.id, Asset.fc_id.is_(None))
    )
    asset_ids = [row[0] for row in result.all()]

    await db.execute(
        delete(UserInstallation).where(UserInstallation.user_id == ctx.user.id)
    )

    if asset_ids:
        await db.execute(
            update(Asset)
            .where(Asset.id.in_(asset_ids))
            .values(deleted_at=datetime.utcnow())
        )

    await db.execute(
        update(User)
        .where(User.id == ctx.user.id)
        .values(
            global_name=None,
            discriminator=None,
            character_name=None,
            world=None,
        )
    )
    await db.execute(
        update(Message)
        .where(Message.author_id == ctx.user.id)
        .values(author_name="[deleted]", author_avatar_url=None)
    )
    await db.execute(
        update(FcUser)
        .where(FcUser.user_id == ctx.user.id)
        .values(consent_sync=False, settings=None)
    )
    await db.commit()
    return {"status": "ok"}
