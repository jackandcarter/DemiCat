import json
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest
from httpx import ASGITransport, AsyncClient

root = Path(__file__).resolve().parents[1] / "demibot"
if str(root) not in sys.path:
    sys.path.append(str(root))

from demibot.db.models import ChannelKind
from demibot.http.api import create_app
from demibot.http.deps import RequestContext, api_key_auth, get_db
import demibot.http.routes.messages as messages_routes


@pytest.mark.asyncio
@pytest.mark.parametrize("channel_key", ["channel_id", "channel"])
async def test_post_message_accepts_channel_aliases(channel_key, monkeypatch):
    app = create_app()

    captured: dict[str, object] = {}

    async def fake_save_message(body, ctx, db, *, channel_kind, files=None):  # type: ignore[override]
        captured["body"] = body
        captured["channel_id"] = body.channel_id
        captured["channel_kind"] = channel_kind
        return {"ok": True}

    monkeypatch.setattr(messages_routes, "save_message", fake_save_message)

    user_ctx = SimpleNamespace(id=1)
    guild_ctx = SimpleNamespace(id=2)

    async def override_auth():
        return RequestContext(user=user_ctx, guild=guild_ctx, key=None, roles=["chat"])

    async def override_db():
        yield None

    app.dependency_overrides[api_key_auth] = override_auth
    app.dependency_overrides[get_db] = override_db

    transport = ASGITransport(app=app)
    async with AsyncClient(transport=transport, base_url="http://test") as client:
        resp = await client.post(
            "/api/messages",
            json={channel_key: "123", "content": "Hello"},
        )

    app.dependency_overrides.clear()

    assert resp.status_code == 200
    data = resp.json()
    assert data["ok"] is True
    assert captured["channel_id"] == "123"
    assert isinstance(captured["body"], messages_routes.PostBody)
    assert captured["channel_kind"] == ChannelKind.FC_CHAT


@pytest.mark.asyncio
async def test_channel_messages_multipart_accepts_message_reference(monkeypatch):
    app = create_app()

    captured: dict[str, object] = {}

    async def fake_save_message(body, ctx, db, *, channel_kind, files=None):  # type: ignore[override]
        captured["body"] = body
        captured["channel_kind"] = channel_kind
        captured["files"] = files
        return {"ok": True, "id": "42"}

    monkeypatch.setattr(messages_routes, "save_message", fake_save_message)

    user_ctx = SimpleNamespace(id=1)
    guild_ctx = SimpleNamespace(id=2)

    async def override_auth():
        return RequestContext(user=user_ctx, guild=guild_ctx, key=None, roles=["chat"])

    async def override_db():
        yield None

    app.dependency_overrides[api_key_auth] = override_auth
    app.dependency_overrides[get_db] = override_db

    transport = ASGITransport(app=app)
    async with AsyncClient(transport=transport, base_url="http://test") as client:
        files = [("files", ("hi.txt", b"hello", "text/plain"))]
        data = {
            "content": "Hello",
            "useCharacterName": "true",
            "message_reference": json.dumps({"messageId": "5", "channelId": "555"}),
        }
        resp = await client.post(
            "/api/channels/555/messages",
            data=data,
            files=files,
        )

    app.dependency_overrides.clear()

    assert resp.status_code == 200
    assert resp.json() == {"ok": True, "id": "42"}

    body = captured["body"]
    assert isinstance(body, messages_routes.PostBody)
    assert body.channel_id == "555"
    assert body.use_character_name is True
    assert body.message_reference is not None
    assert body.message_reference.channel_id == "555"
    assert body.message_reference.message_id == "5"

    assert captured["channel_kind"] == ChannelKind.FC_CHAT

    files = captured["files"]
    assert files is not None and len(files) == 1
    upload = files[0]
    assert getattr(upload, "filename", None) == "hi.txt"
