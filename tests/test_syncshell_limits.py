import asyncio

import pytest

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import User
from demibot.http.deps import RequestContext

from .syncshell_import import syncshell
from .syncshell_test_utils import build_publish_payload


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
            payload, file_path, sha = build_publish_payload(
                tmp_path, discord_id=str(user.discord_user_id)
            )
            syncshell._transfer_budgets.clear()
            response = await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
            assert response["missing"] == [sha]

            blob_path = syncshell._blob_path(sha)
            blob_path.parent.mkdir(parents=True, exist_ok=True)
            blob_path.write_bytes(file_path.read_bytes())

            payload["complete"] = True
            complete = await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
            assert complete["missing"] == []

            await asyncio.sleep(1.1)
            payload["complete"] = False
            with pytest.raises(syncshell.HTTPException):
                await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
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
            payload, _, _ = build_publish_payload(
                tmp_path, discord_id=str(user.discord_user_id)
            )
            syncshell._transfer_budgets.clear()
            response = await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
            assert response["missing"]
            with pytest.raises(syncshell.HTTPException):
                await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
    asyncio.run(_run())
