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
