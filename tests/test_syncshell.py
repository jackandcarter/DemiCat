import asyncio

import pytest

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import User, SyncshellPairing
from demibot.http.deps import RequestContext

from .syncshell_import import syncshell
from .syncshell_test_utils import build_manifest_payload


async def _prepare_db():
    db_session._engine = None
    db_session._Session = None
    await init_db("sqlite+aiosqlite://")
    return get_session()


def test_pair_token_persistence_and_expiry(tmp_path):
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            syncshell.TOKEN_TTL = 1
            resp = await syncshell.pair(ctx=ctx, db=db)
            token = resp["token"]
            pairing = await db.get(SyncshellPairing, user.id)
            assert pairing and pairing.token == token

            manifest, _ = build_manifest_payload(tmp_path)
            await syncshell.upload_manifest(manifest, ctx=ctx, db=db)

            await asyncio.sleep(1.1)
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert exc.value.status_code == 401
    asyncio.run(_run())


def test_manifest_rate_limit(tmp_path):
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            syncshell.RATE_LIMIT = 2
            await syncshell.pair(ctx=ctx, db=db)
            manifest, _ = build_manifest_payload(tmp_path)
            await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert exc.value.status_code == 429
    asyncio.run(_run())


def test_manifest_too_large(tmp_path):
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 10
            await syncshell.pair(ctx=ctx, db=db)
            big_manifest, _ = build_manifest_payload(tmp_path)
            big_manifest.setdefault("meta", []).append({"key": "x", "value": "y" * 50})
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.upload_manifest(big_manifest, ctx=ctx, db=db)
            assert exc.value.status_code == 413
    asyncio.run(_run())


def test_asset_upload_download_and_rate_limit(tmp_path):
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            syncshell.RATE_LIMIT = 4

            async def fake_upload():
                return "upload-url"

            async def fake_download(asset_id):
                return f"download-url-{asset_id}"

            syncshell.presign_upload = fake_upload
            syncshell.presign_download = fake_download

            await syncshell.pair(ctx=ctx, db=db)
            manifest, _ = build_manifest_payload(tmp_path)
            await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            up = await syncshell.request_asset_upload(ctx=ctx, db=db)
            assert up["url"] == "upload-url"
            down = await syncshell.request_asset_download("x", ctx=ctx, db=db)
            assert down["url"] == "download-url-x"
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.request_asset_upload(ctx=ctx, db=db)
            assert exc.value.status_code == 429
    asyncio.run(_run())
