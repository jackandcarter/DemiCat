from __future__ import annotations

import asyncio
import logging
from datetime import datetime, timedelta

from sqlalchemy import delete

from .db.session import get_session
from .db.models import Asset

PURGE_INTERVAL = 3600


async def purge_deleted_assets_once(retention_days: int = 30) -> None:
    async for db in get_session():
        cutoff = datetime.utcnow() - timedelta(days=retention_days)
        await db.execute(delete(Asset).where(Asset.deleted_at < cutoff))
        await db.commit()
        break


async def purge_deleted_assets() -> None:
    while True:
        try:
            await purge_deleted_assets_once()
        except Exception:  # pragma: no cover - best effort
            logging.exception("Asset purge failed")
        await asyncio.sleep(PURGE_INTERVAL)
