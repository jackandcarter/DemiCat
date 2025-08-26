from __future__ import annotations

import hashlib
import hmac
import os
from datetime import datetime

from fastapi import APIRouter, Depends, Response, Request
from sqlalchemy import select, func
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy.orm import selectinload

from ..deps import get_db, api_key_auth, RequestContext
from ...db.models import (
    AppearanceBundle,
    AppearanceBundleItem,
    Asset,
    FcUser,
    IndexCheckpoint,
)

router = APIRouter(prefix="/api")

_SECRET = os.environ.get("ASSET_SIGNING_SECRET", "devsecret").encode()


def _sign_download(asset_id: int, asset_hash: str) -> str:
    sig = hmac.new(_SECRET, f"{asset_id}:{asset_hash}".encode(), hashlib.sha256).hexdigest()
    return f"/assets/{asset_hash}?asset_id={asset_id}&sig={sig}"


async def _update_last_pull(db: AsyncSession, fc_id: int, user_id: int) -> None:
    res = await db.execute(
        select(FcUser).where(FcUser.fc_id == fc_id, FcUser.user_id == user_id)
    )
    fcu = res.scalar_one_or_none()
    if fcu is not None:
        fcu.last_pull_at = datetime.utcnow()


@router.get("/fc/{fc_id}/bundles")
async def list_bundles(
    fc_id: int,
    response: Response,
    request: Request,
    since: datetime | None = None,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    """List appearance bundles for a free company."""

    cp_res = await db.execute(select(func.max(IndexCheckpoint.last_generated_at)))
    last_generated = cp_res.scalar_one_or_none()
    if last_generated is not None:
        etag = last_generated.isoformat()
        if request.headers.get("if-none-match") == etag:
            await _update_last_pull(db, fc_id, ctx.user.id)
            await db.commit()
            return Response(status_code=304, headers={"ETag": etag})
        response.headers["ETag"] = etag

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

    await _update_last_pull(db, fc_id, ctx.user.id)
    await db.commit()
    return {"items": payload}
