import asyncio
import json
from datetime import datetime, timedelta

from fastapi.testclient import TestClient

from demibot.http.api import create_app
from demibot.db.models import (
    Guild,
    Membership,
    SyncshellManifest,
    SyncshellPairing,
    User,
    UserKey,
)
from demibot.db.session import get_session, init_db
import demibot.db.session as db_session

from .syncshell_import import syncshell
from .syncshell_test_utils import build_manifest_payload


async def _prepare_db() -> None:
    db_session._engine = None
    db_session._Session = None
    await init_db("sqlite+aiosqlite://")

    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        user = User(id=1, discord_user_id=1234, global_name="Tester")
        key = UserKey(
            id=1,
            user_id=user.id,
            guild_id=guild.id,
            token="syncshell-token",
            enabled=True,
        )
        membership = Membership(id=1, guild_id=guild.id, user_id=user.id)
        pairing = SyncshellPairing(
            user_id=user.id,
            token="pair",
            created_at=datetime.utcnow(),
            expires_at=datetime.utcnow() + timedelta(minutes=5),
        )
        db.add_all([guild, user, key, membership, pairing])
        await db.commit()


def test_syncshell_websocket_hello_and_manifest(tmp_path):
    asyncio.run(_prepare_db())

    manifest, blob_hash = build_manifest_payload(tmp_path)
    app = create_app()
    client = TestClient(app)

    syncshell._transfer_budgets.clear()

    with client.websocket_connect(
        "/ws/syncshell", headers={"X-Api-Key": "syncshell-token"}
    ) as websocket:
        websocket.send_json(
            {
                "type": "hello",
                "payload": {
                    "version": 1,
                    "limits": {
                        "bytesPerSecond": 512 * 1024,
                        "chunkSizeBytes": 64 * 1024,
                        "maxOutstandingWants": 64,
                    },
                },
            }
        )
        server_hello = websocket.receive_json()
        assert server_hello["type"] == "hello"
        assert server_hello["payload"]["limits"]["chunkSizeBytes"] == 64 * 1024

        websocket.send_json({"type": "manifest", "payload": {"manifest": manifest}})
        want = websocket.receive_json()
        assert want["type"] == "want"
        payload = want["payload"]
        assert blob_hash in payload["want"]["blobs"]
        assert payload["diff"]["need"]
        expected_hints = [
            {"hash": entry["hash"], "size": entry["size"]}
            for entry in payload["diff"]["need"]
            if entry.get("hash") and isinstance(entry.get("size"), int) and entry["size"] > 0
        ]
        assert payload["want"]["sizeHints"] == expected_hints
        assert payload["limits"]["budget"]["limitBytes"] > 0

    async def _fetch_manifest() -> dict:
        async with get_session() as db:
            record = await db.get(SyncshellManifest, 1)
            assert record is not None
            return json.loads(record.manifest_json)

    stored_manifest = asyncio.run(_fetch_manifest())
    assert stored_manifest["protocolVersion"] == 1
