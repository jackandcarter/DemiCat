from __future__ import annotations

import os
from uuid import uuid4
from typing import Any
from datetime import datetime, timedelta
import json

from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import SyncshellPairing, SyncshellManifest, SyncshellRateLimit
from ..vault import presign_upload, presign_download


router = APIRouter(prefix="/api/syncshell", tags=["syncshell"])

RATE_LIMIT = int(os.getenv("SYNC_SHELL_MAX_REQUESTS_PER_MINUTE", "30"))
TOKEN_TTL = int(os.getenv("SYNC_SHELL_TOKEN_TTL", "300"))  # seconds
MAX_MANIFEST_BYTES = 1024 * 1024  # 1 MiB manifest payload cap


async def _check_rate_limit(user_id: int, db: AsyncSession) -> None:
    now = datetime.utcnow()
    record = await db.get(SyncshellRateLimit, user_id)
    if record and (now - record.window_start).total_seconds() < 60:
        if record.requests >= RATE_LIMIT:
            raise HTTPException(status_code=429, detail="rate limit exceeded")
        record.requests += 1
    else:
        if record:
            record.requests = 1
            record.window_start = now
        else:
            record = SyncshellRateLimit(user_id=user_id, requests=1, window_start=now)
            db.add(record)
    await db.commit()


async def _require_pairing(ctx: RequestContext, db: AsyncSession) -> None:
    pairing = await db.get(SyncshellPairing, ctx.user.id)
    if not pairing or pairing.expires_at < datetime.utcnow():
        raise HTTPException(status_code=401, detail="pairing token expired")


@router.post("/pair")
async def pair(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    """Issue a short-lived pairing token for a client."""
    await _check_rate_limit(ctx.user.id, db)
    token = uuid4().hex
    pairing = await db.get(SyncshellPairing, ctx.user.id)
    if pairing:
        pairing.token = token
        pairing.created_at = datetime.utcnow()
        pairing.expires_at = datetime.utcnow() + timedelta(seconds=TOKEN_TTL)
    else:
        pairing = SyncshellPairing(
            user_id=ctx.user.id,
            token=token,
            created_at=datetime.utcnow(),
            expires_at=datetime.utcnow() + timedelta(seconds=TOKEN_TTL),
        )
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
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
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
    db: AsyncSession = Depends(get_db),
) -> dict[str, str]:
    """Return a pre-signed URL for chunked asset upload."""
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    try:
        url = await presign_upload()
    except Exception as e:  # pragma: no cover - network failure
        raise HTTPException(status_code=502, detail="vault unavailable") from e
    return {"url": url}


@router.get("/asset/download/{asset_id}")
async def request_asset_download(
    asset_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, str]:
    """Return a pre-signed URL for asset download."""
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
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
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    await _clear_manifest(ctx, db)
    return {"status": "ok"}


@router.post("/cache")
async def clear_cache(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, str]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    await _clear_manifest(ctx, db)
    return {"status": "ok"}
