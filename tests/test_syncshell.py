import asyncio
import logging
from datetime import datetime
from typing import Any

import pytest

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import (
    User,
    SyncshellPairing,
    SyncshellManifest,
    SyncshellMember,
    SyncshellScope,
)
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
            member = User(id=2, discord_user_id=2, global_name="Member")
            db.add_all([user, member])
            await db.commit()

            now = datetime.utcnow()
            db.add_all(
                [
                    SyncshellMember(
                        user_id=user.id,
                        member_user_id=member.id,
                        created_at=now,
                        scope=int(SyncshellScope.HASHES | SyncshellScope.ASSETS),
                    ),
                    SyncshellMember(
                        user_id=member.id,
                        member_user_id=user.id,
                        created_at=now,
                        scope=int(SyncshellScope.HASHES | SyncshellScope.ASSETS),
                    ),
                ]
            )
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            syncshell.TOKEN_TTL = 1
            resp = await syncshell.pair(ctx=ctx, db=db)
            token = resp["token"]
            pairing = await db.get(SyncshellPairing, user.id)
            assert pairing and pairing.token == token

            manifest, _ = build_manifest_payload(tmp_path)
            await syncshell._reset_transfer_budgets(db)
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
            member = User(id=2, discord_user_id=2, global_name="Member")
            db.add_all([user, member])
            await db.commit()

            now = datetime.utcnow()
            db.add_all(
                [
                    SyncshellMember(
                        user_id=user.id,
                        member_user_id=member.id,
                        created_at=now,
                        scope=int(SyncshellScope.HASHES | SyncshellScope.ASSETS),
                    ),
                    SyncshellMember(
                        user_id=member.id,
                        member_user_id=user.id,
                        created_at=now,
                        scope=int(SyncshellScope.HASHES | SyncshellScope.ASSETS),
                    ),
                ]
            )
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            syncshell.RATE_LIMIT = 2
            await syncshell.pair(ctx=ctx, db=db)
            manifest, _ = build_manifest_payload(tmp_path)
            await syncshell._reset_transfer_budgets(db)
            first = await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert first["diff"]["need"]
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert exc.value.status_code == 429
    asyncio.run(_run())


def test_manifest_concurrent_upload_budget_upsert(tmp_path):
    async def _run():
        session_factory = await _prepare_db()
        manifest: dict[str, Any] | None = None

        async with session_factory as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            await syncshell.pair(ctx=ctx, db=db)

            manifest, _ = build_manifest_payload(tmp_path)
            await syncshell._reset_transfer_budgets(db)
            db.add(SyncshellManifest(user_id=user.id, manifest_json="{}"))
            await db.commit()
            user_id = user.id

        assert manifest is not None

        async def _upload_once() -> dict[str, Any]:
            async with get_session() as db:
                db_user = await db.get(User, user_id)
                assert db_user is not None
                local_ctx = RequestContext(user=db_user, guild=None, key=object(), roles=[])
                return await syncshell.upload_manifest(manifest, ctx=local_ctx, db=db)

        first, second = await asyncio.gather(_upload_once(), _upload_once())

        assert first["status"] == "ok"
        assert second["status"] == "ok"

    asyncio.run(_run())


def test_manifest_denylist_blocks_transfers(monkeypatch, tmp_path):
    manifest, _ = build_manifest_payload(tmp_path)
    monkeypatch.setattr(syncshell, "SYNC_SHELL_DENYLIST", frozenset({"sample-mod"}))

    assets = syncshell._extract_manifest_assets(manifest)
    file_key = ("file", "default", "sample-mod", "character.mtrl")
    assert file_key in assets
    assert assets[file_key]["hash"]
    assert assets[file_key]["transferable"] is False

    diff = syncshell._compute_manifest_diff(None, manifest)
    assert diff["need"] == []


def test_manifest_denylist_blocks_signatures(monkeypatch, tmp_path):
    manifest, file_hash = build_manifest_payload(tmp_path)
    mod_hash = manifest["collections"][0]["mods"][0]["hash"]
    monkeypatch.setattr(syncshell, "SYNC_SHELL_DENYLIST", frozenset({mod_hash, file_hash}))

    assets = syncshell._extract_manifest_assets(manifest)
    file_key = ("file", "default", "sample-mod", "character.mtrl")
    mod_key = ("mod", "default", "sample-mod")
    assert assets[file_key]["transferable"] is False
    assert assets[mod_key]["hash"] == mod_hash

    diff = syncshell._compute_manifest_diff(None, manifest)
    assert diff["need"] == []


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
            member = User(id=2, discord_user_id=2, global_name="Member")
            db.add_all([user, member])
            await db.commit()

            now = datetime.utcnow()
            db.add_all(
                [
                    SyncshellMember(
                        user_id=user.id,
                        member_user_id=member.id,
                        created_at=now,
                        scope=int(SyncshellScope.HASHES | SyncshellScope.ASSETS),
                    ),
                    SyncshellMember(
                        user_id=member.id,
                        member_user_id=user.id,
                        created_at=now,
                        scope=int(SyncshellScope.HASHES | SyncshellScope.ASSETS),
                    ),
                ]
            )
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
            await syncshell._reset_transfer_budgets(db)
            response = await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert response["diff"]["need"]
            up = await syncshell.request_asset_upload(ctx=ctx, db=db)
            assert up["url"] == "upload-url"
            down = await syncshell.request_asset_download("x", ctx=ctx, db=db)
            assert down["url"] == "download-url-x"
            with pytest.raises(syncshell.HTTPException) as exc:
                await syncshell.request_asset_upload(ctx=ctx, db=db)
            assert exc.value.status_code == 429
    asyncio.run(_run())


def test_asset_presign_rejected_when_budget_exhausted(tmp_path):
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user = User(id=10, discord_user_id=10, global_name="Budgetless")
            member = User(id=11, discord_user_id=11, global_name="Member")
            db.add_all([user, member])
            await db.commit()

            now = datetime.utcnow()
            db.add_all(
                [
                    SyncshellMember(
                        user_id=user.id,
                        member_user_id=member.id,
                        created_at=now,
                        scope=int(SyncshellScope.HASHES | SyncshellScope.ASSETS),
                    ),
                    SyncshellMember(
                        user_id=member.id,
                        member_user_id=user.id,
                        created_at=now,
                        scope=int(SyncshellScope.HASHES | SyncshellScope.ASSETS),
                    ),
                ]
            )
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            original_budget = syncshell.TRANSFER_BUDGET_BYTES
            original_window = syncshell.TRANSFER_BUDGET_WINDOW_SECONDS
            syncshell.TRANSFER_BUDGET_BYTES = 30
            syncshell.TRANSFER_BUDGET_WINDOW_SECONDS = 3600

            async def fail_upload():
                raise AssertionError("presign_upload should not be called")

            async def fail_download(asset_id):
                raise AssertionError("presign_download should not be called")

            original_presign_upload = syncshell.presign_upload
            original_presign_download = syncshell.presign_download
            syncshell.presign_upload = fail_upload
            syncshell.presign_download = fail_download

            try:
                await syncshell.pair(ctx=ctx, db=db)
                manifest, _ = build_manifest_payload(tmp_path)
                await syncshell._reset_transfer_budgets(db)

                with pytest.raises(syncshell.HTTPException) as manifest_exc:
                    await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
                assert manifest_exc.value.status_code == 429

                with pytest.raises(syncshell.HTTPException) as upload_exc:
                    await syncshell.request_asset_upload(ctx=ctx, db=db)
                assert upload_exc.value.status_code == 429

                with pytest.raises(syncshell.HTTPException) as download_exc:
                    await syncshell.request_asset_download("asset", ctx=ctx, db=db)
                assert download_exc.value.status_code == 429
            finally:
                syncshell.TRANSFER_BUDGET_BYTES = original_budget
                syncshell.TRANSFER_BUDGET_WINDOW_SECONDS = original_window
                syncshell.presign_upload = original_presign_upload
                syncshell.presign_download = original_presign_download

    asyncio.run(_run())


def test_asset_scope_required_for_presign(tmp_path):
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            owner = User(id=1, discord_user_id=1, global_name="Owner")
            grantor = User(id=2, discord_user_id=2, global_name="Grantor")
            db.add_all([owner, grantor])
            await db.commit()

            db.add(
                SyncshellMember(
                    user_id=grantor.id,
                    member_user_id=owner.id,
                    created_at=datetime.utcnow(),
                    scope=int(SyncshellScope.HASHES),
                )
            )
            await db.commit()

            ctx = RequestContext(user=owner, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            syncshell.RATE_LIMIT = 4
            await syncshell.pair(ctx=ctx, db=db)

            with pytest.raises(syncshell.HTTPException) as download_error:
                await syncshell.request_asset_download("hash", ctx=ctx, db=db)
            assert download_error.value.status_code == 403

            with pytest.raises(syncshell.HTTPException) as upload_error:
                await syncshell.request_asset_upload(ctx=ctx, db=db)
            assert upload_error.value.status_code == 403

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
            await syncshell._reset_transfer_budgets(db)

            caplog.set_level(logging.WARNING, logger="demibot.http.routes.syncshell")
            response = await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            assert response["status"] == "ok"

    asyncio.run(_run())
    assert any(
        "syncshell.manifest.previous.decode_failed" in record.getMessage()
        for record in caplog.records
    )
