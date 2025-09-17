import asyncio
import json
from pathlib import Path
import types

from sqlalchemy import select

from demibot.db.models import Guild, GuildChannel, ChannelKind
from demibot.db.session import init_db, get_session
from demibot.db import session as db_session
from demibot.http.deps import RequestContext
from demibot.http.routes import channels as channel_routes


def _setup_db(
    path: str,
    *,
    extra_channels: list[tuple[int, ChannelKind, str | None]] | None = None,
) -> None:
    db_path = Path(path)
    if db_path.exists():
        db_path.unlink()
    db_session._engine = None
    db_session._Session = None
    url = f"sqlite+aiosqlite:///{db_path}"
    asyncio.run(init_db(url))

    async def populate() -> None:
        async with get_session() as db:
            guild = Guild(id=1, discord_guild_id=1, name="Test")
            db.add(guild)
            channels = [
                GuildChannel(
                    guild_id=guild.id,
                    channel_id=100,
                    kind=ChannelKind.EVENT,
                    name="12345",
                )
            ]
            for channel_id, kind, name in extra_channels or []:
                channels.append(
                    GuildChannel(
                        guild_id=guild.id,
                        channel_id=channel_id,
                        kind=kind,
                        name=name,
                    )
                )
            db.add_all(channels)
            await db.commit()

    asyncio.run(populate())


def test_get_channels_returns_placeholder_and_flags_retry(monkeypatch):
    _setup_db("test_channels.db")

    async def fake_ensure_channel_name(*args, **kwargs):
        return None

    monkeypatch.setattr(channel_routes, "ensure_channel_name", fake_ensure_channel_name)

    async def dummy_broadcast(*args, **kwargs):
        return None

    monkeypatch.setattr(channel_routes.manager, "broadcast_text", dummy_broadcast)

    class Dummy:
        pass

    async def run():
        async with get_session() as db:
            guild = (await db.execute(select(Guild).where(Guild.id == 1))).scalar_one()
            ctx = RequestContext(user=Dummy(), guild=guild, key=Dummy(), roles=[])
            resp = await channel_routes.get_channels(ctx=ctx, db=db)
            data = json.loads(resp.body.decode())
            row = (
                await db.execute(
                    select(GuildChannel).where(
                        GuildChannel.guild_id == 1,
                        GuildChannel.channel_id == 100,
                        GuildChannel.kind == ChannelKind.EVENT,
                    )
                )
            ).scalar_one()
            return data, row.name

    data, name = asyncio.run(run())
    assert data[ChannelKind.EVENT.value][0]["name"] == "100"
    assert name is None


def test_get_channels_kind_event(monkeypatch):
    _setup_db("test_channels2.db")

    async def fake_ensure_channel_name(*args, **kwargs):
        return "chan"

    monkeypatch.setattr(channel_routes, "ensure_channel_name", fake_ensure_channel_name)

    async def dummy_broadcast(*args, **kwargs):
        return None

    monkeypatch.setattr(channel_routes.manager, "broadcast_text", dummy_broadcast)

    class Dummy:
        pass

    async def run():
        async with get_session() as db:
            guild = (await db.execute(select(Guild).where(Guild.id == 1))).scalar_one()
            ctx = RequestContext(user=Dummy(), guild=guild, key=Dummy(), roles=[])
            resp = await channel_routes.get_channels(kind="event", ctx=ctx, db=db)
            return json.loads(resp.body.decode())

    data = asyncio.run(run())
    assert isinstance(data, list)
    assert data[0]["name"] == "chan"


def test_get_channels_skips_forum_and_archived(monkeypatch):
    db_path = "test_channels3.db"
    path = Path(db_path)
    if path.exists():
        path.unlink()
    db_session._engine = None
    db_session._Session = None
    url = f"sqlite+aiosqlite:///{db_path}"
    asyncio.run(init_db(url))

    async def populate() -> None:
        async with get_session() as db:
            guild = Guild(id=1, discord_guild_id=1, name="Test")
            db.add(guild)
            db.add_all(
                [
                    GuildChannel(guild_id=1, channel_id=200, kind=ChannelKind.EVENT, name="forum"),
                    GuildChannel(guild_id=1, channel_id=201, kind=ChannelKind.EVENT, name="thread"),
                    GuildChannel(guild_id=1, channel_id=202, kind=ChannelKind.EVENT, name="arch"),
                ]
            )
            await db.commit()

    asyncio.run(populate())

    names = {200: "forum", 201: "thread", 202: "arch"}

    async def fake_ensure_channel_name(db, guild_id, channel_id, kind, name):
        return names[channel_id]

    monkeypatch.setattr(channel_routes, "ensure_channel_name", fake_ensure_channel_name)

    async def dummy_broadcast(*args, **kwargs):
        return None

    monkeypatch.setattr(channel_routes.manager, "broadcast_text", dummy_broadcast)

    class DummyForum:
        name = "forum"

    class DummyParent:
        name = "parent"

    class DummyThread:
        def __init__(self, name: str, archived: bool = False):
            self.name = name
            self.archived = archived
            self.parent = DummyParent()

    class DummyClient:
        def get_channel(self, cid: int):
            mapping = {
                200: DummyForum(),
                201: DummyThread("thread"),
                202: DummyThread("arch", archived=True),
            }
            return mapping.get(cid)

    monkeypatch.setattr(channel_routes, "discord_client", DummyClient())
    monkeypatch.setattr(
        channel_routes,
        "discord",
        types.SimpleNamespace(ForumChannel=DummyForum, Thread=DummyThread),
    )

    class Dummy:
        pass

    async def run():
        async with get_session() as db:
            guild = (await db.execute(select(Guild).where(Guild.id == 1))).scalar_one()
            ctx = RequestContext(user=Dummy(), guild=guild, key=Dummy(), roles=[])
            resp = await channel_routes.get_channels(kind="event", ctx=ctx, db=db)
            return json.loads(resp.body.decode())

    data = asyncio.run(run())
    assert data == [{"id": "201", "name": "parent / thread"}]


def test_validate_channel_success_and_wrong_kind():
    _setup_db("test_channels_validate.db")

    class Dummy:
        pass

    async def run():
        async with get_session() as db:
            guild = (await db.execute(select(Guild).where(Guild.id == 1))).scalar_one()
            ctx = RequestContext(user=Dummy(), guild=guild, key=Dummy(), roles=[])
            ok_resp = await channel_routes.validate_channel(
                100, "EVENT", ctx=ctx, db=db
            )
            wrong_kind = await channel_routes.validate_channel(
                100, "FC_CHAT", ctx=ctx, db=db
            )
            return (
                json.loads(ok_resp.body.decode()),
                json.loads(wrong_kind.body.decode()),
            )

    ok_data, wrong_data = asyncio.run(run())
    assert ok_data == {
        "ok": True,
        "guildId": "1",
        "kind": ChannelKind.EVENT.value,
        "name": "12345",
    }
    assert wrong_data == {"ok": False, "reason": "WRONG_KIND"}


def test_validate_channel_invalid_kind_and_missing():
    _setup_db("test_channels_validate_missing.db")

    class Dummy:
        pass

    async def run():
        async with get_session() as db:
            guild = (await db.execute(select(Guild).where(Guild.id == 1))).scalar_one()
            ctx = RequestContext(user=Dummy(), guild=guild, key=Dummy(), roles=[])
            invalid = await channel_routes.validate_channel(
                100, "unknown", ctx=ctx, db=db
            )
            missing = await channel_routes.validate_channel(
                999, "EVENT", ctx=ctx, db=db
            )
            return (
                json.loads(invalid.body.decode()),
                json.loads(missing.body.decode()),
            )

    invalid_data, missing_data = asyncio.run(run())
    assert invalid_data == {"ok": False, "reason": "INVALID_KIND"}
    assert missing_data == {"ok": False, "reason": "NOT_FOUND"}


def test_validate_channel_officer_requires_role():
    _setup_db(
        "test_channels_validate_officer.db",
        extra_channels=[(200, ChannelKind.OFFICER_CHAT, "officer")],
    )

    class Dummy:
        pass

    async def run():
        async with get_session() as db:
            guild = (await db.execute(select(Guild).where(Guild.id == 1))).scalar_one()
            no_role_ctx = RequestContext(user=Dummy(), guild=guild, key=Dummy(), roles=[])
            forbidden = await channel_routes.validate_channel(
                200, "OFFICER_CHAT", ctx=no_role_ctx, db=db
            )
            officer_ctx = RequestContext(
                user=Dummy(), guild=guild, key=Dummy(), roles=["officer"]
            )
            allowed = await channel_routes.validate_channel(
                200, "OFFICER_CHAT", ctx=officer_ctx, db=db
            )
            return (
                json.loads(forbidden.body.decode()),
                json.loads(allowed.body.decode()),
            )

    forbidden_data, allowed_data = asyncio.run(run())
    assert forbidden_data == {"ok": False, "reason": "FORBIDDEN"}
    assert allowed_data == {
        "ok": True,
        "guildId": "1",
        "kind": ChannelKind.OFFICER_CHAT.value,
        "name": "officer",
    }

