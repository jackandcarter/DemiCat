from __future__ import annotations

import hashlib
import hmac
import os
from datetime import datetime

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy.orm import selectinload

from ..deps import get_db
from ...db.models import AppearanceBundle, AppearanceBundleItem, Asset

router = APIRouter(prefix="/api")

_SECRET = os.environ.get("ASSET_SIGNING_SECRET", "devsecret").encode()


def _sign_download(asset_id: int, asset_hash: str) -> str:
    sig = hmac.new(_SECRET, f"{asset_id}:{asset_hash}".encode(), hashlib.sha256).hexdigest()
    return f"/assets/{asset_hash}?asset_id={asset_id}&sig={sig}"


@router.get("/fc/{fc_id}/bundles")
async def list_bundles(
    fc_id: int,
    since: datetime | None = None,
    db: AsyncSession = Depends(get_db),
):
    """List appearance bundles for a free company."""

    stmt = (
        select(AppearanceBundle)
        .options(
            selectinload(AppearanceBundle.items).selectinload(AppearanceBundleItem.asset)
        )
        .where(AppearanceBundle.fc_id == fc_id)
    )
    if since is not None:
        stmt = stmt.where(AppearanceBundle.updated_at >= since)
    result = await db.execute(stmt)
    bundles = result.scalars().unique().all()
    payload = []
    for b in bundles:
        assets = []
        for item in b.items:
            a: Asset = item.asset
            assets.append(
                {
                    "id": a.id,
                    "kind": a.kind.value,
                    "name": a.name,
                    "hash": a.hash,
                    "size": a.size,
                    "quantity": item.quantity,
                    "download_url": _sign_download(a.id, a.hash),
                }
            )
        payload.append(
            {
                "id": b.id,
                "name": b.name,
                "description": b.description,
                "updated_at": b.updated_at.isoformat() if b.updated_at else None,
                "assets": assets,
            }
        )
    return {"items": payload}
