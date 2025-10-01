import sys
import types
from pathlib import Path
import asyncio

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

routes_pkg = types.ModuleType("demibot.http.routes")
routes_pkg.__path__ = [str(root / "demibot/http/routes")]
sys.modules.setdefault("demibot.http.routes", routes_pkg)

# stub discord dependency
import types as _types

discord_mod = _types.ModuleType("discord")
abc_mod = _types.ModuleType("discord.abc")
abc_mod.Messageable = type("Messageable", (), {})
discord_mod.abc = abc_mod
 

class _DummyEmbed:
    def __init__(self, *args, **kwargs):
        self.args = args
        self.kwargs = kwargs
        self.fields = []

    def add_field(self, *args, **kwargs):
        self.fields.append((args, kwargs))


discord_mod.Embed = _DummyEmbed
discord_mod.ClientException = type("ClientException", (Exception,), {})


class _DummyHTTPException(Exception):
    def __init__(self, status: int = 0, text: str | None = None):
        super().__init__(text)
        self.status = status
        self.text = text


discord_mod.HTTPException = _DummyHTTPException
sys.modules["discord"] = discord_mod
sys.modules["discord.abc"] = abc_mod
ext_mod = _types.ModuleType("discord.ext")
commands_mod = _types.ModuleType("discord.ext.commands")
ext_mod.commands = commands_mod
discord_mod.ext = ext_mod
sys.modules["discord.ext"] = ext_mod
sys.modules["discord.ext.commands"] = commands_mod

from types import SimpleNamespace

from demibot.db.models import (
    ChannelKind,
    Guild,
    GuildChannel,
    User,
    Request as DbRequest,
    RequestStatus,
    RequestType,
    Urgency,
)
from demibot.db.session import init_db, get_session
from demibot.http.deps import RequestContext
from demibot.http.discord_client import set_discord_client
import demibot.http.routes.requests as request_routes

async def _setup_db(db_path: str) -> None:
    await init_db(f"sqlite+aiosqlite:///{db_path}")
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        user = User(id=1, discord_user_id=1)
        db.add_all([guild, user])
        await db.commit()

import pytest

@pytest.fixture()
def db_setup(tmp_path):
    path = tmp_path / "requests.db"
    asyncio.run(_setup_db(str(path)))
    return path


def test_requests_delta(db_setup):
    async def run():
        async with get_session() as db:
            guild = await db.get(Guild, 1)
            user = await db.get(User, 1)
            req = DbRequest(
                id=1,
                guild_id=guild.id,
                user_id=user.id,
                title="Test",
                type=RequestType.ITEM,
                status=RequestStatus.OPEN,
                urgency=Urgency.LOW,
            )
            db.add(req)
            await db.commit()
            await db.refresh(req)
            ctx = RequestContext(user=user, guild=guild, key=SimpleNamespace(), roles=[])
            since = req.updated_at
            # no changes yet
            res1 = await request_routes.list_request_deltas(since=since, ctx=ctx, db=db)
            # update request
            req.title = "Updated"
            await db.commit()
            since2 = req.updated_at
            res2 = await request_routes.list_request_deltas(since=since, ctx=ctx, db=db)
            # delete request
            await request_routes.delete_request(request_id=req.id, ctx=ctx, db=db)
            res3 = await request_routes.list_request_deltas(since=since2, ctx=ctx, db=db)
            return res1, res2, res3
    first, second, third = asyncio.run(run())
    assert first == []
    assert len(second) == 1
    assert second[0]["title"] == "Updated"
    assert len(third) == 1
    assert third[0]["deleted"] is True
    assert third[0]["id"] == "1"


def test_notify_posts_to_requests_channel_when_discord_guild_id_present(
    db_setup, monkeypatch
):
    async def run():
        async with get_session() as db:
            guild = await db.get(Guild, 1)
            user = await db.get(User, 1)
            discord_id = 987654321012345678
            guild.discord_guild_id = discord_id
            req = DbRequest(
                id=2,
                guild_id=guild.id,
                user_id=user.id,
                title="Notify",
                type=RequestType.ITEM,
                status=RequestStatus.OPEN,
                urgency=Urgency.LOW,
            )
            db.add(req)
            await db.commit()
            await db.refresh(req)
            db.add(
                GuildChannel(
                    guild_id=guild.id,
                    channel_id=123,
                    kind=ChannelKind.REQUESTS,
                    name="requests",
                )
            )
            await db.commit()
            ctx = RequestContext(user=user, guild=guild, key=SimpleNamespace(), roles=[])

            async def fake_send_dm(*args, **kwargs):
                return None

            monkeypatch.setattr(request_routes, "_send_dm", fake_send_dm)

            class DummyChannel:
                def __init__(self) -> None:
                    self.id = 123
                    self.name = "requests"
                    self.sent = []

                async def send(self, *args, **kwargs):
                    self.sent.append((args, kwargs))

            channel = DummyChannel()

            class DummyGuild:
                def __init__(self, channel_obj) -> None:
                    self._channel = channel_obj
                    self.text_channels = [channel_obj]

                def get_channel(self, channel_id: int):
                    if channel_id == self._channel.id:
                        return self._channel
                    return None

            dummy_guild = DummyGuild(channel)

            class DummyClient:
                def __init__(self) -> None:
                    self.calls: list[int] = []

                def get_guild(self, guild_id: int):
                    self.calls.append(guild_id)
                    return dummy_guild

            dummy_client = DummyClient()
            set_discord_client(dummy_client)
            try:
                await request_routes._notify(
                    ctx.guild.discord_guild_id, req, "created", db, ctx.user
                )
            finally:
                set_discord_client(None)

            return channel.sent, dummy_client.calls, discord_id

    sent_messages, calls, expected_id = asyncio.run(run())
    assert calls == [expected_id]
    assert len(sent_messages) == 1
