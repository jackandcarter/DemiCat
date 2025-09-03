from __future__ import annotations

import asyncio
import logging
from datetime import datetime, timedelta

from sqlalchemy import delete

from .db.session import get_session
from .db.models import SyncshellPairing, SyncshellRateLimit

PRUNE_INTERVAL = 60


async def prune_syncshell_once() -> None:
    async with get_session() as db:
        now = datetime.utcnow()
        await db.execute(delete(SyncshellPairing).where(SyncshellPairing.expires_at < now))
        cutoff = now - timedelta(minutes=5)
        await db.execute(
            delete(SyncshellRateLimit).where(SyncshellRateLimit.window_start < cutoff)
        )
        await db.commit()
        break


async def prune_syncshell() -> None:
    while True:
        try:
            await prune_syncshell_once()
        except Exception:  # pragma: no cover - best effort
            logging.exception("Syncshell cleanup failed")
        await asyncio.sleep(PRUNE_INTERVAL)
