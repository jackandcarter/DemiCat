import asyncio
import hashlib
import json

import pytest

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import User, SyncshellPairing, SyncshellManifest
from demibot.http.deps import RequestContext

from .syncshell_import import syncshell
from .syncshell_test_utils import build_manifest_payload, build_publish_payload


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

            payload, file_path, sha = build_publish_payload(
                tmp_path, discord_id=str(user.discord_user_id)
            )
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            initial = await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
            assert initial["missing"] == [sha]

            blob_path = syncshell._blob_path(sha)
            blob_path.parent.mkdir(parents=True, exist_ok=True)
            blob_path.write_bytes(file_path.read_bytes())

            payload["complete"] = True
            complete = await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
            assert complete["missing"] == []

            record = await db.get(SyncshellManifest, user.id)
            assert record is not None
            stored = json.loads(record.manifest_json)
            stored_blob = stored["appearance"]["blobs"][0]
            assert stored_blob["sha256"] == sha

            await asyncio.sleep(1.1)
            payload["complete"] = False
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
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
            payload, _, _ = build_publish_payload(
                tmp_path, discord_id=str(user.discord_user_id)
            )
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            syncshell._transfer_budgets.clear()
            first = await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
            assert first["missing"]
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
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
            payload, _, _ = build_publish_payload(
                tmp_path, discord_id=str(user.discord_user_id)
            )
            payload["appearance"]["glamourer"] = "x" * 100
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
            assert exc.value.status_code == 413
    asyncio.run(_run())


def test_publish_overwrites_corrupted_manifest(tmp_path):
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])

            await syncshell.pair(ctx=ctx, db=db)

            corrupted = SyncshellManifest(user_id=user.id, manifest_json="{invalid}")
            db.add(corrupted)
            await db.commit()

            payload, file_path, sha = build_publish_payload(
                tmp_path, discord_id=str(user.discord_user_id)
            )
            blob_path = syncshell._blob_path(sha)
            blob_path.parent.mkdir(parents=True, exist_ok=True)
            blob_path.write_bytes(file_path.read_bytes())

            payload["complete"] = True
            result = await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
            assert result["missing"] == []

            updated = await db.get(SyncshellManifest, user.id)
            assert updated is not None
            stored = json.loads(updated.manifest_json)
            assert stored["appearance"]["blobs"][0]["sha256"] == sha

    asyncio.run(_run())


def test_publish_accepts_legacy_manifest(tmp_path):
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])

            await syncshell.pair(ctx=ctx, db=db)

            manifest, file_hash = build_manifest_payload(tmp_path)
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024

            result = await syncshell.handle_publish_manifest(manifest, ctx=ctx, db=db)

            assert result["status"] == "ok"
            record = await db.get(SyncshellManifest, user.id)
            assert record is not None
            stored = json.loads(record.manifest_json)
            stored_hash = (
                stored.get("collections", [{}])[0]
                .get("mods", [{}])[0]
                .get("files", [{}])[0]
                .get("hash")
            )
            assert stored_hash == file_hash

    asyncio.run(_run())
