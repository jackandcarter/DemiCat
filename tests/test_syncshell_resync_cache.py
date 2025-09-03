import asyncio

from demibot.db.session import init_db, get_session
from demibot.db.models import User, SyncshellManifest
from demibot.http.deps import RequestContext
from demibot.http.routes.syncshell import upload_manifest, resync, clear_cache


def test_resync_and_cache_clear():
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async for db in get_session():
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            manifest = [{"id": "a"}]
            await upload_manifest(manifest, ctx=ctx, db=db)
            assert await db.get(SyncshellManifest, 1) is not None

            await clear_cache(ctx=ctx, db=db)
            assert await db.get(SyncshellManifest, 1) is None

            await upload_manifest(manifest, ctx=ctx, db=db)
            assert await db.get(SyncshellManifest, 1) is not None

            await resync(ctx=ctx, db=db)
            assert await db.get(SyncshellManifest, 1) is None
            break
    asyncio.run(_run())
