import asyncio
import json

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import User, SyncshellManifest
from demibot.http.deps import RequestContext

from .syncshell_import import syncshell
from .syncshell_test_utils import build_publish_payload


def test_manifest_blob_hashes_persist(tmp_path):
    async def _run():
        db_session._engine = None
        db_session._Session = None
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            user = User(id=10, discord_user_id=10, global_name="BlobTester")
            db.add(user)
            await db.commit()

            ctx = RequestContext(user=user, guild=None, key=object(), roles=[])
            syncshell.MAX_MANIFEST_BYTES = 1024 * 1024
            syncshell.RATE_LIMIT = 5
            await syncshell.pair(ctx=ctx, db=db)

            payload, file_path, blob_hash = build_publish_payload(
                tmp_path, discord_id=str(user.discord_user_id)
            )
            syncshell._transfer_budgets.clear()
            response = await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
            assert response["missing"] == [blob_hash]

            blob_path = syncshell._blob_path(blob_hash)
            blob_path.parent.mkdir(parents=True, exist_ok=True)
            blob_path.write_bytes(file_path.read_bytes())

            payload["complete"] = True
            complete = await syncshell.handle_publish_manifest(payload, ctx=ctx, db=db)
            assert complete["missing"] == []
            record = await db.get(SyncshellManifest, user.id)
            assert record is not None
            stored = json.loads(record.manifest_json)
            assert stored["appearance"]["blobs"][0]["sha256"] == blob_hash

            newer_payload, newer_path, newer_hash = build_publish_payload(
                tmp_path / "second", discord_id=str(user.discord_user_id)
            )
            newer_response = await syncshell.handle_publish_manifest(
                newer_payload, ctx=ctx, db=db
            )
            assert newer_response["missing"] == [newer_hash]

            newer_blob_path = syncshell._blob_path(newer_hash)
            newer_blob_path.parent.mkdir(parents=True, exist_ok=True)
            newer_blob_path.write_bytes(newer_path.read_bytes())

            newer_payload["complete"] = True
            newer_complete = await syncshell.handle_publish_manifest(
                newer_payload, ctx=ctx, db=db
            )
            assert newer_complete["missing"] == []

            updated = await db.get(SyncshellManifest, user.id)
            assert updated is not None
            updated_payload = json.loads(updated.manifest_json)
            stored_blob = updated_payload["appearance"]["blobs"][0]
            assert stored_blob["sha256"] == newer_hash
    asyncio.run(_run())
