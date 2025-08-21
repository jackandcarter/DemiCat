import asyncio
from pathlib import Path
import logging
import types

import pytest
from sqlalchemy import select

from demibot import channel_names as cn
from demibot.db.models import Guild, GuildChannel
from demibot.db.session import init_db, get_session


def _setup_db(path: str) -> None:
    db_path = Path(path)
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    asyncio.run(init_db(url))

    async def populate() -> None:
        async for db in get_session():
            guild = Guild(id=1, discord_guild_id=1, name="Test")
            db.add(guild)
            db.add(GuildChannel(guild_id=guild.id, channel_id=100, kind="event", name=None))
            await db.commit()
            break

    asyncio.run(populate())


def test_fetch_channel_updates_name():
    _setup_db("test1.db")

    class DummyChannel:
        name = "resolved"

    class DummyClient:
        def get_channel(self, _):
            return None

        async def fetch_channel(self, _):
            return DummyChannel()

    cn.discord_client = DummyClient()

    async def run() -> str | None:
        async for db in get_session():
            await cn.ensure_channel_name(db, 1, 100, "event", None)
            row = (
                await db.execute(
                    select(GuildChannel).where(
                        GuildChannel.guild_id == 1,
                        GuildChannel.channel_id == 100,
                        GuildChannel.kind == "event",
                    )
                )
            ).scalar_one()
            return row.name

    name = asyncio.run(run())
    assert name == "resolved"


def test_rest_fallback_updates_name(monkeypatch):
    _setup_db("test_rest.db")

    cn.discord_client = None

    class DummyResponse:
        status = 200

        async def json(self):
            return {"name": "rest"}

        async def __aenter__(self):
            return self

        async def __aexit__(self, exc_type, exc, tb):
            return False

    class DummySession:
        async def __aenter__(self):
            return self

        async def __aexit__(self, exc_type, exc, tb):
            return False

        def get(self, url, headers=None):
            assert headers and "Authorization" in headers
            return DummyResponse()

    monkeypatch.setattr(cn, "aiohttp", types.SimpleNamespace(ClientSession=DummySession))
    monkeypatch.setattr(cn, "load_config", lambda: types.SimpleNamespace(discord_token="t"))

    async def run() -> str | None:
        async for db in get_session():
            await cn.ensure_channel_name(db, 1, 100, "event", None)
            row = (
                await db.execute(
                    select(GuildChannel.name).where(
                        GuildChannel.guild_id == 1,
                        GuildChannel.channel_id == 100,
                        GuildChannel.kind == "event",
                    )
                )
            ).scalar_one()
            return row

    name = asyncio.run(run())
    assert name == "rest"


def test_retry_null_channel_names_logs_failure(caplog):
    _setup_db("test2.db")

    class DummyClient:
        def get_channel(self, _):
            return None

        async def fetch_channel(self, _):
            raise RuntimeError("boom")

    cn.discord_client = DummyClient()
    caplog.set_level(logging.WARNING)

    asyncio.run(cn.retry_null_channel_names(max_attempts=2))

    async def get_name() -> str | None:
        async for db in get_session():
            row = (
                await db.execute(
                    select(GuildChannel.name).where(
                        GuildChannel.guild_id == 1,
                        GuildChannel.channel_id == 100,
                        GuildChannel.kind == "event",
                    )
                )
            ).scalar_one()
            return row

    name = asyncio.run(get_name())
    assert name is None
    assert any("Channel name missing" in r.message for r in caplog.records)
