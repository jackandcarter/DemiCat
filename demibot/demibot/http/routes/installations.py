from __future__ import annotations

from datetime import datetime

from fastapi import APIRouter, Depends
from pydantic import BaseModel, Field, ConfigDict
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import Asset, InstallStatus, UserInstallation

router = APIRouter(prefix="/api")


class InstallationPayload(BaseModel):
    """Payload for creating or updating a user installation."""

    asset_id: int = Field(alias="assetId")
    status: InstallStatus
    asset_hash: str | None = Field(default=None, alias="assetHash")

    model_config = ConfigDict(populate_by_name=True)


@router.get("/users/me/installations")
async def get_my_installations(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    """List installation records for the authenticated user."""

    stmt = select(UserInstallation).where(UserInstallation.user_id == ctx.user.id)
    result = await db.execute(stmt)
    rows = result.scalars().all()
    items = [
        {
            "assetId": str(row.asset_id),
            "status": row.status.value,
            "updatedAt": row.updated_at.isoformat() if row.updated_at else None,
            "assetHash": row.asset_hash,
        }
        for row in rows
    ]
    return items


@router.post("/users/me/installations")
async def post_my_installations(
    payload: InstallationPayload,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    """Create or update an installation record for the authenticated user."""

    stmt = select(UserInstallation).where(
        UserInstallation.user_id == ctx.user.id,
        UserInstallation.asset_id == payload.asset_id,
    )
    result = await db.execute(stmt)
    inst = result.scalar_one_or_none()
    asset_hash = payload.asset_hash
    if not asset_hash:
        asset_res = await db.execute(
            select(Asset.hash).where(Asset.id == payload.asset_id)
        )
        asset_hash = asset_res.scalar_one_or_none()
    now = datetime.utcnow()
    if inst is None:
        inst = UserInstallation(
            user_id=ctx.user.id,
            asset_id=payload.asset_id,
            status=payload.status,
            asset_hash=asset_hash,
            updated_at=now,
        )
        db.add(inst)
    else:
        inst.status = payload.status
        inst.asset_hash = asset_hash
        inst.updated_at = now
    await db.commit()
    await db.refresh(inst)
    return {
        "assetId": str(inst.asset_id),
        "status": inst.status.value,
        "updatedAt": inst.updated_at.isoformat() if inst.updated_at else None,
        "assetHash": inst.asset_hash,
    }
