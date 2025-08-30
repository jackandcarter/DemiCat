from __future__ import annotations

import time
from uuid import uuid4
from typing import Any

from fastapi import APIRouter, Depends, HTTPException

from ..deps import RequestContext, api_key_auth


router = APIRouter(prefix="/api/syncshell", tags=["syncshell"])

# Simple in-memory stores for demonstration purposes only.
_pair_tokens: dict[int, str] = {}
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
async def pair(ctx: RequestContext = Depends(api_key_auth)) -> dict[str, Any]:
    """Issue a short-lived pairing token for a client."""
    _check_rate_limit(ctx.user.id)
    token = uuid4().hex
    _pair_tokens[ctx.user.id] = token
    return {"token": token}


@router.post("/manifest")
async def upload_manifest(
    manifest: list[dict[str, Any]],
    ctx: RequestContext = Depends(api_key_auth),
) -> dict[str, Any]:
    """Receive a hashed file manifest from a client.

    The manifest is capped in size to prevent excessive memory usage and
    naive clients from overwhelming the API.
    """
    _check_rate_limit(ctx.user.id)
    payload_size = len(str(manifest).encode())
    if payload_size > MAX_MANIFEST_BYTES:
        raise HTTPException(status_code=413, detail="manifest too large")
    # In a real implementation, the manifest would be persisted and compared
    # against server-side data to determine missing assets.
    return {"status": "ok"}


@router.post("/asset/upload")
async def request_asset_upload(ctx: RequestContext = Depends(api_key_auth)) -> dict[str, str]:
    """Return a pre-signed URL for chunked asset upload.

    This is a stub; integration with the Vault service should generate and
    return a short-lived URL that the client can PUT to.
    """
    _check_rate_limit(ctx.user.id)
    return {"url": "https://vault.example/upload"}


@router.get("/asset/download/{asset_id}")
async def request_asset_download(asset_id: str, ctx: RequestContext = Depends(api_key_auth)) -> dict[str, str]:
    """Return a pre-signed URL for asset download."""
    _check_rate_limit(ctx.user.id)
    return {"url": f"https://vault.example/{asset_id}"}
