from datetime import datetime, timedelta
import asyncio

from sqlalchemy import select

from demibot.db.models import Asset, AssetKind
from demibot.db.session import init_db, get_session
from demibot.asset_cleanup import purge_deleted_assets_once


def test_purge_deleted_assets_once():
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            old = Asset(
                id=1,
                kind=AssetKind.FILE,
                name="old",
                hash="h1",
                size=1,
                deleted_at=datetime.utcnow() - timedelta(days=31),
            )
            recent = Asset(
                id=2,
                kind=AssetKind.FILE,
                name="new",
                hash="h2",
                size=1,
                deleted_at=datetime.utcnow(),
            )
            db.add_all([old, recent])
            await db.commit()
            await purge_deleted_assets_once(retention_days=30)
            assets = (await db.execute(select(Asset).order_by(Asset.id))).scalars().all()
            assert len(assets) == 1
            assert assets[0].id == 2
    asyncio.run(_run())
