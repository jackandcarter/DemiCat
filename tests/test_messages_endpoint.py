import json
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest
from httpx import ASGITransport, AsyncClient
from sqlalchemy import text

root = Path(__file__).resolve().parents[1] / "demibot"
if str(root) not in sys.path:
    sys.path.append(str(root))

from demibot.db.models import (
    ChannelKind,
    Guild,
    Membership,
    GuildChannel,
    User,
)
from demibot.db.session import get_session, init_db
from demibot.http.api import create_app
from demibot.http.deps import RequestContext, api_key_auth, get_db
import demibot.http.routes.messages as messages_routes
import demibot.http.routes._messages_common as messages_common


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


@pytest.mark.asyncio
async def test_channel_message_falls_back_when_webhook_forbidden(monkeypatch):
    await init_db("sqlite+aiosqlite://")
    async with get_session() as db:
        await db.execute(text("DELETE FROM posted_messages"))
        await db.execute(text("DELETE FROM messages"))
        await db.execute(text("DELETE FROM memberships"))
        await db.execute(text("DELETE FROM users"))
        await db.execute(text("DELETE FROM guilds"))
        await db.execute(text("DELETE FROM guild_channels"))

        guild_id = 77
        channel_id = 12345
        user_id = 55

        db.add(Guild(id=guild_id, discord_guild_id=770, name="Guild"))
        db.add(User(id=user_id, discord_user_id=550, global_name="Tester"))
        db.add(
            Membership(
                guild_id=guild_id,
                user_id=user_id,
                nickname="TesterNick",
                avatar_url="http://example.com/avatar.png",
            )
        )
        db.add(
            GuildChannel(
                guild_id=guild_id,
                channel_id=channel_id,
                kind=ChannelKind.FC_CHAT,
            )
        )
        await db.commit()

    class DummyChannel:
        def __init__(self) -> None:
            self.id = channel_id
            self.webhook_attempts = 0
            self.sent_payloads: list[dict[str, object]] = []

        async def create_webhook(self, name: str):
            self.webhook_attempts += 1
            raise messages_common.discord.Forbidden(
                SimpleNamespace(status=403, reason="Forbidden"),
                {"message": "Missing Permissions", "code": 50013},
            )

        async def send(self, content: str, **kwargs):
            self.sent_payloads.append({"content": content, "kwargs": kwargs})
            return SimpleNamespace(id=999, attachments=[])

    dummy_channel = DummyChannel()

    class DummyClient:
        def get_channel(self, cid: int):
            return dummy_channel if cid == channel_id else None

    async def dummy_broadcast(message: str, guild_id: int, officer_only: bool = False, path: str | None = None):
        return None

    async def dummy_emit_event(event: dict) -> None:
        return None

    monkeypatch.setattr(messages_common.manager, "broadcast_text", dummy_broadcast)
    monkeypatch.setattr(messages_common, "emit_event", dummy_emit_event)
    monkeypatch.setattr(messages_common, "discord_client", DummyClient())
    monkeypatch.setattr(messages_common.discord, "TextChannel", DummyChannel, raising=False)
    monkeypatch.setattr(messages_common.discord.abc, "Messageable", DummyChannel, raising=False)
    monkeypatch.setattr(messages_common, "_channel_webhooks", {})

    app = create_app()

    user_ctx = SimpleNamespace(id=user_id, global_name="Tester", character_name=None)
    guild_ctx = SimpleNamespace(id=guild_id, discord_guild_id=770)

    async def override_auth():
        return RequestContext(user=user_ctx, guild=guild_ctx, key=None, roles=["chat"])

    async def override_db():
        async with get_session() as session:
            yield session

    app.dependency_overrides[api_key_auth] = override_auth
    app.dependency_overrides[get_db] = override_db

    transport = ASGITransport(app=app)
    async with AsyncClient(transport=transport, base_url="http://test") as client:
        resp = await client.post(
            f"/api/channels/{channel_id}/messages",
            data={"content": "Hello world"},
        )

    app.dependency_overrides.clear()

    assert resp.status_code == 200
    data = resp.json()
    assert data["ok"] is True
    assert dummy_channel.webhook_attempts == 1
    assert len(dummy_channel.sent_payloads) == 1
    assert dummy_channel.sent_payloads[0]["content"] == "Hello world"
    discord_errors = data.get("detail", {}).get("discord")
    assert discord_errors is not None
    assert "Manage Webhooks required" in discord_errors[0]
