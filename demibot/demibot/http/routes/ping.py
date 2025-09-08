from __future__ import annotations

from fastapi import APIRouter, Response

router = APIRouter(prefix="/api")


@router.head("/ping")
@router.get("/ping")
async def ping() -> Response:
    """Simple endpoint for liveness checks."""
    return Response(status_code=200)
