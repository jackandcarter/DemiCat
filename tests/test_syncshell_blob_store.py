import asyncio
import json

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import User, SyncshellManifest
from demibot.http.deps import RequestContext

from .syncshell_import import syncshell
from .syncshell_test_utils import build_manifest_payload


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

            manifest, blob_hash = build_manifest_payload(tmp_path)
            syncshell._transfer_budgets.clear()
            response = await syncshell.upload_manifest(manifest, ctx=ctx, db=db)
            need_hashes = {entry["hash"] for entry in response["diff"]["need"]}
            assert blob_hash in need_hashes
            record = await db.get(SyncshellManifest, user.id)
            assert record is not None
            stored = json.loads(record.manifest_json)
            stored_file = stored["collections"][0]["mods"][0]["files"][0]
            assert stored_file["hash"] == blob_hash
            assert stored["wantBlobs"]["blobs"] == [blob_hash]

            # Updating the manifest replaces the stored payload.
            newer_manifest, newer_hash = build_manifest_payload(tmp_path / "second")
            response = await syncshell.upload_manifest(newer_manifest, ctx=ctx, db=db)
            diff = response["diff"]
            need_hashes = {entry["hash"] for entry in diff["need"]}
            remove_hashes = {entry["hash"] for entry in diff["remove"]}
            assert newer_hash in need_hashes
            assert blob_hash in remove_hashes
            assert diff["conflicts"]
            updated = await db.get(SyncshellManifest, user.id)
            assert updated is not None
            updated_payload = json.loads(updated.manifest_json)
            assert updated_payload["collections"][0]["mods"][0]["files"][0]["hash"] == newer_hash
            assert updated_payload["wantBlobs"]["blobs"] == [newer_hash]
    asyncio.run(_run())
