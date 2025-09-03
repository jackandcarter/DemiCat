import asyncio
import pytest

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import User
from demibot.http.deps import RequestContext
import importlib.util
import sys
from pathlib import Path

syncshell_path = (
    Path(__file__).resolve().parents[1] / "demibot" / "demibot" / "http" / "routes" / "syncshell.py"
)
spec = importlib.util.spec_from_file_location(
    "demibot.http.routes.syncshell", syncshell_path
)
syncshell = importlib.util.module_from_spec(spec)
sys.modules["demibot.http.routes.syncshell"] = syncshell
spec.loader.exec_module(syncshell)


def test_token_expiry():
    async def _run():
        db_session._engine = None
        db_session._Session = None
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            user = User(id=2, discord_user_id=2, global_name="Test2")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.TOKEN_TTL = 1
            await syncshell.pair(ctx=ctx, db=db)
            await asyncio.sleep(1.1)
            with pytest.raises(syncshell.HTTPException):
                await syncshell.upload_manifest([], ctx=ctx, db=db)
            break
    asyncio.run(_run())


def test_rate_limit_hits():
    async def _run():
        db_session._engine = None
        db_session._Session = None
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.RATE_LIMIT = 2
            await syncshell.pair(ctx=ctx, db=db)
            await syncshell.upload_manifest([], ctx=ctx, db=db)
            with pytest.raises(syncshell.HTTPException):
                await syncshell.resync(ctx=ctx, db=db)
            break
    asyncio.run(_run())
