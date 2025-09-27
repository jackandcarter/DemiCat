import asyncio

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import User, SyncshellManifest
from demibot.http.deps import RequestContext

from .syncshell_import import syncshell
from .syncshell_test_utils import build_manifest_payload


def test_resync_and_cache_clear(tmp_path):
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
            syncshell.RATE_LIMIT = 5
            manifest, _ = build_manifest_payload(tmp_path)
            await syncshell.pair(ctx=ctx, db=db)
            await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert await db.get(SyncshellManifest, 1) is not None

            await syncshell.clear_cache(ctx=ctx, db=db)
            assert await db.get(SyncshellManifest, 1) is None

            manifest2, _ = build_manifest_payload(tmp_path / "second")
            await syncshell.upload_manifest(manifest2, ctx=ctx, db=db)
            assert await db.get(SyncshellManifest, 1) is not None

            await syncshell.resync(ctx=ctx, db=db)
            assert await db.get(SyncshellManifest, 1) is None
    asyncio.run(_run())
