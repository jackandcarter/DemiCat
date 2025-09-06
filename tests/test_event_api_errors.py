import asyncio
import json
import sys
import types
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import discord
import pytest

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

# Stub package structure to avoid heavy imports
demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.db.models import Guild, GuildChannel, ChannelKind
from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.http.routes.events import create_event, CreateEventBody


class _DummyChannel(discord.abc.Messageable):
    def __init__(self, fail_times: int) -> None:
        self.fail_times = fail_times
        self.calls = 0

    async def send(self, *args, **kwargs):
        self.calls += 1
        if self.calls <= self.fail_times:
            raise discord.HTTPException(
                SimpleNamespace(status=500, reason="server"), "boom"
            )
        return SimpleNamespace(id=123, embeds=[], attachments=[])

    async def _get_channel(self):  # pragma: no cover - required by Messageable
        return self


class _DummyClient:
    def __init__(self, ch: _DummyChannel) -> None:
        self._ch = ch

    def get_channel(self, cid: int):
        return self._ch


async def _setup_db(tmp_name: str) -> None:
    db_path = Path(tmp_name)
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    db_session._engine = None
    db_session._Session = None
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="G")
        db.add(guild)
        db.add(GuildChannel(guild_id=1, channel_id=123, kind=ChannelKind.EVENT))
        await db.commit()


async def _call_create(body: CreateEventBody, client: _DummyClient):
    ctx = SimpleNamespace(guild=SimpleNamespace(id=1))
    async with get_session() as db:
        original_dumps = json.dumps

        async def noop_sleep(_):
            return None

        with patch("demibot.http.routes.events.json.dumps", lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k)), \
            patch("demibot.http.routes.events.discord_client", client), \
            patch("demibot.http.routes.events.discord.abc.Messageable", _DummyChannel), \
            patch("demibot.discordbot.utils.asyncio.sleep", noop_sleep):
            return await create_event(body=body, ctx=ctx, db=db)


async def _run_success() -> None:
    await _setup_db("test_event_retry_ok.db")
    ch = _DummyChannel(fail_times=2)
    client = _DummyClient(ch)
    body = CreateEventBody(channelId="123", title="T", time="2024-01-01T00:00:00Z", description="d")
    res = await _call_create(body, client)
    assert res["ok"] is True
    assert ch.calls == 3


async def _run_failure() -> None:
    await _setup_db("test_event_retry_fail.db")
    ch = _DummyChannel(fail_times=5)
    client = _DummyClient(ch)
    body = CreateEventBody(channelId="123", title="T", time="2024-01-01T00:00:00Z", description="d")
    res = await _call_create(body, client)
    assert hasattr(res, "status_code") and res.status_code == 502
    data = json.loads(res.body)
    assert data["error"] == "discord_api_error"
    assert data["status"] == 500
    assert ch.calls == 3


def test_retry_success() -> None:
    asyncio.run(_run_success())


def test_retry_failure() -> None:
    asyncio.run(_run_failure())
