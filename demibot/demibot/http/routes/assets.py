from __future__ import annotations

import hashlib
import hmac
import os
from datetime import datetime
from typing import List

from fastapi import APIRouter, Depends, Response
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import get_db
from ...db.models import Asset, AssetKind

router = APIRouter(prefix="/api")

_SECRET = os.environ.get("ASSET_SIGNING_SECRET", "devsecret").encode()


def _sign_download(asset_id: int, asset_hash: str) -> str:
    """Generate a signed download URL for an asset."""
    sig = hmac.new(_SECRET, f"{asset_id}:{asset_hash}".encode(), hashlib.sha256).hexdigest()
    return f"/assets/{asset_id}/download?sig={sig}"


@router.get("/fc/{fc_id}/assets")
async def list_assets(
    fc_id: int,
    response: Response,
    since: datetime | None = None,
    kinds: str | None = None,
    limit: int | None = None,
    cursor: int | None = None,
    db: AsyncSession = Depends(get_db),
):
    """List assets for a free company.

    Parameters
    ----------
    fc_id:
        Free company identifier.
    since:
        Optional ISO8601 timestamp to filter assets updated after this time.
    kinds:
        Comma separated list of asset kinds to include.
    limit:
        Maximum number of assets to return.
    cursor:
        Only return assets with an ID greater than this value.
    """

    stmt = select(Asset).where(Asset.fc_id == fc_id)
    if since is not None:
        stmt = stmt.where(Asset.updated_at >= since)
    if cursor is not None:
        stmt = stmt.where(Asset.id > cursor)
    if kinds:
        kind_list: List[str] = [k.strip() for k in kinds.split(",") if k.strip()]
        try:
            kind_enum = [AssetKind(k) for k in kind_list]
            stmt = stmt.where(Asset.kind.in_(kind_enum))
        except ValueError:
            # Ignore invalid kinds
            pass
    stmt = stmt.order_by(Asset.id.asc())
    if limit is not None:
        stmt = stmt.limit(limit)
    result = await db.execute(stmt)
    assets = result.scalars().all()
    items = []
    for a in assets:
        items.append(
            {
                "id": a.id,
                "kind": a.kind.value,
                "name": a.name,
                "hash": a.hash,
                "size": a.size,
                "updated_at": a.updated_at.isoformat() if a.updated_at else None,
                "download_url": _sign_download(a.id, a.hash),
            }
        )
    if items:
        etag_src = ",".join(str(i["id"]) for i in items)
        response.headers["ETag"] = hashlib.sha256(etag_src.encode()).hexdigest()
    return {"items": items}
