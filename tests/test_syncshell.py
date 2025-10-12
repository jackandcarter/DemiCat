import asyncio
import logging

import pytest

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import User, SyncshellPairing, SyncshellManifest
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
            syncshell._transfer_budgets.clear()
            response = await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert response["status"] == "ok"
            assert response["diff"]["need"]
            assert response["diff"]["remove"] == []
            assert response["limits"]["budget"]["limitBytes"] > 0

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
            syncshell._transfer_budgets.clear()
            first = await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert first["diff"]["need"]
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


def test_manifest_corrupt_previous_logs_warning(tmp_path, caplog):
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            syncshell.RATE_LIMIT = 5

            await syncshell.pair(ctx=ctx, db=db)

            corrupted = SyncshellManifest(user_id=user.id, manifest_json="{invalid}")
            db.add(corrupted)
            await db.commit()

            manifest, _ = build_manifest_payload(tmp_path)
            syncshell._transfer_budgets.clear()

            caplog.set_level(logging.WARNING, logger="demibot.http.routes.syncshell")
            response = await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert response["status"] == "ok"

    asyncio.run(_run())
    assert any(
        "syncshell.manifest.previous.decode_failed" in record.getMessage()
        for record in caplog.records
    )
