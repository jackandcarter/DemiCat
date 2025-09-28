import asyncio

import pytest

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import User
from demibot.http.deps import RequestContext

from .syncshell_import import syncshell
from .syncshell_test_utils import build_manifest_payload


def test_token_expiry(tmp_path):
    async def _run():
        db_session._engine = None
        db_session._Session = None
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            user = User(id=2, discord_user_id=2, global_name="Test2")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            syncshell.TOKEN_TTL = 1
            await syncshell.pair(ctx=ctx, db=db)
            manifest, _ = build_manifest_payload(tmp_path)
            syncshell._transfer_budgets.clear()
            response = await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert response["diff"]["need"]
            await asyncio.sleep(1.1)
            with pytest.raises(syncshell.HTTPException):
                await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
    asyncio.run(_run())


def test_rate_limit_hits(tmp_path):
    async def _run():
        db_session._engine = None
        db_session._Session = None
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            syncshell.RATE_LIMIT = 2
            await syncshell.pair(ctx=ctx, db=db)
            manifest, _ = build_manifest_payload(tmp_path)
            syncshell._transfer_budgets.clear()
            response = await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert response["diff"]["need"]
            assert response["limits"]["budget"]["windowEndsAt"].endswith("Z")
            with pytest.raises(syncshell.HTTPException):
                await syncshell.resync(ctx=ctx, db=db)
    asyncio.run(_run())
