import asyncio
import hashlib
import json

from fastapi import FastAPI
from fastapi.testclient import TestClient

from demibot.db.models import Guild, Membership, SyncshellManifest, User, UserKey
from demibot.db.session import get_session, init_db
import demibot.db.session as db_session

from .syncshell_import import syncshell


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
        db.add_all([guild, user, key, membership])
        await db.commit()


def _make_client(tmp_path, monkeypatch):
    blob_root = tmp_path / "blobs"
    monkeypatch.setattr(syncshell, "BLOB_ROOT", blob_root)
    asyncio.run(_prepare_db())
    app = FastAPI()
    app.include_router(syncshell.router)
    return TestClient(app)


def test_blob_upload_head_get_and_range(tmp_path, monkeypatch):
    client = _make_client(tmp_path, monkeypatch)
    payload = b"syncshell-test"
    sha = hashlib.sha256(payload).hexdigest()
    headers = {"X-Api-Key": "syncshell-token"}

    response = client.put(
        f"/api/syncshell/blobs?sha256={sha}", headers=headers, data=payload
    )
    assert response.status_code == 201
    assert response.headers.get("Content-Length") == str(len(payload))

    second = client.put(f"/api/syncshell/blobs?sha256={sha}", headers=headers, data=payload)
    assert second.status_code == 204

    head = client.head(f"/api/syncshell/blobs?sha256={sha}", headers=headers)
    assert head.status_code == 200
    assert head.headers.get("Content-Length") == str(len(payload))

    whole = client.get(f"/api/syncshell/blobs?sha256={sha}", headers=headers)
    assert whole.status_code == 200
    assert whole.content == payload

    ranged = client.get(
        f"/api/syncshell/blobs?sha256={sha}",
        headers={"X-Api-Key": "syncshell-token", "Range": "bytes=0-3"},
    )
    assert ranged.status_code == 206
    assert ranged.headers.get("Content-Range") == f"bytes 0-3/{len(payload)}"
    assert ranged.content == payload[:4]


def test_blob_upload_mismatch_rejected(tmp_path, monkeypatch):
    client = _make_client(tmp_path, monkeypatch)
    payload = b"syncshell"
    sha = hashlib.sha256(b"other").hexdigest()

    response = client.put(
        f"/api/syncshell/blobs?sha256={sha}",
        headers={"X-Api-Key": "syncshell-token"},
        data=payload,
    )
    assert response.status_code == 400


def test_meta_returns_latest_manifest(tmp_path, monkeypatch):
    client = _make_client(tmp_path, monkeypatch)
    payload = {
        "appearance": {
            "actorHash": "actor-1",
            "glamourer": "{\"foo\":1}",
            "blobs": [
                {
                    "name": "mod/file",
                    "sha256": hashlib.sha256(b"asset").hexdigest(),
                    "size": 5,
                }
            ],
        }
    }

    async def _store_manifest() -> None:
        async with get_session() as db:
            record = SyncshellManifest(
                user_id=1,
                manifest_json=json.dumps(payload),
            )
            db.add(record)
            await db.commit()

    asyncio.run(_store_manifest())

    response = client.get(
        "/api/syncshell/meta",
        headers={"X-Api-Key": "syncshell-token"},
        params={"discordId": "1234"},
    )
    assert response.status_code == 200
    body = response.json()
    assert body["discordId"] == "1234"
    assert body["appearance"]["actorHash"] == "actor-1"
    assert body["appearance"]["blobs"][0]["name"] == "mod/file"
