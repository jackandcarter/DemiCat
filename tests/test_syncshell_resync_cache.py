import asyncio

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import User, SyncshellManifest
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


def test_resync_and_cache_clear():
    async def _run():
        db_session._engine = None
        db_session._Session = None
        await init_db("sqlite+aiosqlite://")
        async for db in get_session():
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            manifest = [{"id": "a"}]
            await syncshell.pair(ctx=ctx, db=db)
            await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert await db.get(SyncshellManifest, 1) is not None

            await syncshell.clear_cache(ctx=ctx, db=db)
            assert await db.get(SyncshellManifest, 1) is None

            await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert await db.get(SyncshellManifest, 1) is not None

            await syncshell.resync(ctx=ctx, db=db)
            assert await db.get(SyncshellManifest, 1) is None
            break
    asyncio.run(_run())
