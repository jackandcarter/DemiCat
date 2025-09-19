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

from demibot.db.models import Guild, User, Membership, GuildChannel, ChannelKind
from demibot.db.session import get_session, init_db
from demibot.http.api import create_app
from demibot.http.deps import RequestContext, api_key_auth, get_db
import demibot.http.routes._messages_common as mc
import demibot.http.routes.officer_messages as officer_routes


@pytest.mark.asyncio
async def test_officer_unresolved_returns_409(monkeypatch):
    await init_db("sqlite+aiosqlite://")
    async with get_session() as db:
        await db.execute(text("DELETE FROM messages"))
        await db.execute(text("DELETE FROM memberships"))
        await db.execute(text("DELETE FROM users"))
        await db.execute(text("DELETE FROM guild_channels"))
        await db.execute(text("DELETE FROM guilds"))
        guild = Guild(id=1, discord_guild_id=101, name="Guild")
        user = User(id=1, discord_user_id=201, global_name="Officer")
        membership = Membership(
            guild_id=guild.id,
            user_id=user.id,
            nickname="Officer",
            avatar_url="http://example.com/avatar.png",
        )
        channel = GuildChannel(
            guild_id=guild.id,
            channel_id=555,
            kind=ChannelKind.OFFICER_CHAT,
        )
        db.add_all([guild, user, membership, channel])
        await db.commit()

    class DummyClient:
        def get_channel(self, channel_id):
            return None

        def get_guild(self, guild_id):
            return None

        async def fetch_channel(self, channel_id):
            return None

    monkeypatch.setattr(mc, "discord_client", DummyClient())
    monkeypatch.setattr(mc, "_channel_webhooks", {})

    app = create_app()

    user_ctx = SimpleNamespace(
        id=1,
        discord_user_id=201,
        global_name="Officer",
        character_name=None,
    )
    guild_ctx = SimpleNamespace(id=1, discord_guild_id=101)

    async def override_auth():
        return RequestContext(user=user_ctx, guild=guild_ctx, key=None, roles=["officer"])

    async def override_db():
        async with get_session() as session:
            yield session

    app.dependency_overrides[api_key_auth] = override_auth
    app.dependency_overrides[get_db] = override_db

    transport = ASGITransport(app=app)
    async with AsyncClient(transport=transport, base_url="http://test") as client:
        resp = await client.post(
            "/api/officer-messages",
            json={"channelId": "555", "content": "Hello"},
        )

    assert resp.status_code == 409
    assert resp.json() == {
        "detail": {
            "code": "OFFICER_CHANNEL_UNRESOLVED",
            "message": "Officer channel could not be resolved",
            "channelId": "555",
        }
    }


@pytest.mark.asyncio
async def test_officer_multipart_uses_officer_endpoint(monkeypatch):
    app = create_app()

    captured: dict[str, object] = {}

    async def fake_save_message(body, ctx, db, *, channel_kind, files=None):  # type: ignore[override]
        captured["body"] = body
        captured["ctx"] = ctx
        captured["channel_kind"] = channel_kind
        captured["files"] = files
        return {"ok": True, "id": "77"}

    monkeypatch.setattr(officer_routes, "save_message", fake_save_message)

    user_ctx = SimpleNamespace(id=1)
    guild_ctx = SimpleNamespace(id=2)

    async def override_auth():
        return RequestContext(user=user_ctx, guild=guild_ctx, key=None, roles=["officer"])

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
            "/api/channels/555/officer-messages",
            data=data,
            files=files,
        )

    app.dependency_overrides.clear()

    assert resp.status_code == 200
    assert resp.json() == {"ok": True, "id": "77"}

    body = captured["body"]
    assert isinstance(body, mc.PostBody)
    assert body.channel_id == "555"
    assert body.use_character_name is True
    assert body.message_reference is not None
    assert body.message_reference.channel_id == "555"
    assert body.message_reference.message_id == "5"

    assert captured["channel_kind"] == ChannelKind.OFFICER_CHAT

    files = captured["files"]
    assert files is not None and len(files) == 1
    upload = files[0]
    assert getattr(upload, "filename", None) == "hi.txt"
