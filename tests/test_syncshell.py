import asyncio
import pytest

import importlib.util
import sys
from pathlib import Path
import types

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "demibot"))

structlog_stub = types.SimpleNamespace(
    processors=types.SimpleNamespace(
        TimeStamper=lambda fmt=None: None,
        add_log_level=lambda *a, **k: None,
        EventRenamer=lambda *a, **k: None,
        JSONRenderer=lambda *a, **k: None,
    ),
    make_filtering_bound_logger=lambda *a, **k: None,
    stdlib=types.SimpleNamespace(LoggerFactory=lambda: None),
    configure=lambda *a, **k: None,
    get_logger=lambda *a, **k: None,
)
sys.modules.setdefault("structlog", structlog_stub)

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import User, SyncshellPairing
from demibot.http.deps import RequestContext

syncshell_path = (
    Path(__file__).resolve().parents[1] / "demibot" / "demibot" / "http" / "routes" / "syncshell.py"
)
spec = importlib.util.spec_from_file_location(
    "demibot.http.routes.syncshell", syncshell_path
)
syncshell = importlib.util.module_from_spec(spec)
sys.modules["demibot.http.routes.syncshell"] = syncshell
spec.loader.exec_module(syncshell)


async def _prepare_db():
    db_session._engine = None
    db_session._Session = None
    await init_db("sqlite+aiosqlite://")
    return get_session()


def test_pair_token_persistence_and_expiry():
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.TOKEN_TTL = 1
            resp = await syncshell.pair(ctx=ctx, db=db)
            token = resp["token"]
            pairing = await db.get(SyncshellPairing, user.id)
            assert pairing and pairing.token == token

            await asyncio.sleep(1.1)
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.upload_manifest([], ctx=ctx, db=db)
            assert exc.value.status_code == 401
    asyncio.run(_run())


def test_manifest_rate_limit():
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.RATE_LIMIT = 2
            await syncshell.pair(ctx=ctx, db=db)
            await syncshell.upload_manifest([], ctx=ctx, db=db)
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.upload_manifest([], ctx=ctx, db=db)
            assert exc.value.status_code == 429
    asyncio.run(_run())


def test_manifest_too_large():
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 10
            await syncshell.pair(ctx=ctx, db=db)
            big_manifest = [{"id": "a" * 20}]
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.upload_manifest(big_manifest, ctx=ctx, db=db)
            assert exc.value.status_code == 413
    asyncio.run(_run())


def test_asset_upload_download_and_rate_limit():
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.RATE_LIMIT = 3

            async def fake_upload():
                return "upload-url"

            async def fake_download(asset_id):
                return f"download-url-{asset_id}"

            syncshell.presign_upload = fake_upload
            syncshell.presign_download = fake_download

            await syncshell.pair(ctx=ctx, db=db)
            up = await syncshell.request_asset_upload(ctx=ctx, db=db)
            assert up["url"] == "upload-url"
            down = await syncshell.request_asset_download("x", ctx=ctx, db=db)
            assert down["url"] == "download-url-x"
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.request_asset_upload(ctx=ctx, db=db)
            assert exc.value.status_code == 429
    asyncio.run(_run())
