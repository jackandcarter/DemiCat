from __future__ import annotations

import time
from uuid import uuid4
from typing import Any
from datetime import datetime
import json

from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import SyncshellPairing, SyncshellManifest
from ..vault import presign_upload, presign_download


router = APIRouter(prefix="/api/syncshell", tags=["syncshell"])

_rate_limiter: dict[int, list[float]] = {}

RATE_LIMIT = 30  # max requests per minute per user
MAX_MANIFEST_BYTES = 1024 * 1024  # 1 MiB manifest payload cap


def _check_rate_limit(user_id: int) -> None:
    """Very small in-memory rate limiter."""
    now = time.time()
    bucket = _rate_limiter.setdefault(user_id, [])
    bucket[:] = [t for t in bucket if now - t < 60]
    if len(bucket) >= RATE_LIMIT:
        raise HTTPException(status_code=429, detail="rate limit exceeded")
    bucket.append(now)


@router.post("/pair")
async def pair(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    """Issue a short-lived pairing token for a client."""
    _check_rate_limit(ctx.user.id)
    token = uuid4().hex
    pairing = await db.get(SyncshellPairing, ctx.user.id)
    if pairing:
        pairing.token = token
        pairing.created_at = datetime.utcnow()
    else:
        pairing = SyncshellPairing(user_id=ctx.user.id, token=token)
        db.add(pairing)
    await db.commit()
    return {"token": token}


@router.post("/manifest")
async def upload_manifest(
    manifest: list[dict[str, Any]],
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    """Receive a hashed file manifest from a client.

    The manifest is capped in size to prevent excessive memory usage and
    naive clients from overwhelming the API.
    """
    _check_rate_limit(ctx.user.id)
    payload_size = len(str(manifest).encode())
    if payload_size > MAX_MANIFEST_BYTES:
        raise HTTPException(status_code=413, detail="manifest too large")
    manifest_json = json.dumps(manifest)
    record = await db.get(SyncshellManifest, ctx.user.id)
    if record:
        record.manifest_json = manifest_json
        record.updated_at = datetime.utcnow()
    else:
        record = SyncshellManifest(user_id=ctx.user.id, manifest_json=manifest_json)
        db.add(record)
    await db.commit()
    return {"status": "ok"}


@router.post("/asset/upload")
async def request_asset_upload(
    ctx: RequestContext = Depends(api_key_auth),
) -> dict[str, str]:
    """Return a pre-signed URL for chunked asset upload."""
    _check_rate_limit(ctx.user.id)
    try:
        url = await presign_upload()
    except Exception as e:  # pragma: no cover - network failure
        raise HTTPException(status_code=502, detail="vault unavailable") from e
    return {"url": url}


@router.get("/asset/download/{asset_id}")
async def request_asset_download(
    asset_id: str, ctx: RequestContext = Depends(api_key_auth)
) -> dict[str, str]:
    """Return a pre-signed URL for asset download."""
    _check_rate_limit(ctx.user.id)
    try:
        url = await presign_download(asset_id)
    except Exception as e:  # pragma: no cover - network failure
        raise HTTPException(status_code=502, detail="vault unavailable") from e
    return {"url": url}


async def _clear_manifest(ctx: RequestContext, db: AsyncSession) -> None:
    record = await db.get(SyncshellManifest, ctx.user.id)
    if record:
        await db.delete(record)
        await db.commit()


@router.post("/resync")
async def resync(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, str]:
    _check_rate_limit(ctx.user.id)
    await _clear_manifest(ctx, db)
    return {"status": "ok"}


@router.post("/cache")
async def clear_cache(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, str]:
    _check_rate_limit(ctx.user.id)
    await _clear_manifest(ctx, db)
    return {"status": "ok"}
