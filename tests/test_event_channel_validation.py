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

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.db.models import Guild, GuildChannel, ChannelKind
from demibot.db.session import init_db, get_session
from demibot.http.routes.events import create_event, CreateEventBody
from fastapi import HTTPException


async def _setup_db(path: str, *, kind: ChannelKind | None) -> None:
    db_path = Path(path)
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="G")
        db.add(guild)
        if kind is not None:
            db.add(GuildChannel(guild_id=1, channel_id=456, kind=kind))
        await db.commit()


async def _call_create(body: CreateEventBody):
    ctx = SimpleNamespace(guild=SimpleNamespace(id=1), roles=[])
    async with get_session() as db:
        original_dumps = json.dumps
        with patch(
            "demibot.http.routes.events.json.dumps",
            lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
        ):
            return await create_event(body=body, ctx=ctx, db=db)


def test_create_event_requires_event_channel() -> None:
    async def run() -> None:
        await _setup_db("test_event_invalid_kind.db", kind=ChannelKind.CHAT)
        body = CreateEventBody(channelId="456", title="T", description="d")
        with pytest.raises(HTTPException):
            await _call_create(body)
    asyncio.run(run())


class DummyTextChannel(discord.abc.Messageable):
    def __init__(self) -> None:
        self.id = 123

    async def send(self, *args, **kwargs):  # pragma: no cover - should not be called
        return SimpleNamespace(id=999, embeds=[], attachments=[])

    async def _get_channel(self):  # pragma: no cover - required by Messageable
        return self


class DummyThread(discord.abc.Messageable):
    def __init__(self, parent: DummyTextChannel) -> None:
        self.parent = parent
        self.id = 456

    async def _get_channel(self):  # pragma: no cover - required by Messageable
        return self


def test_create_event_thread_uses_webhook() -> None:
    async def run() -> None:
        await _setup_db("test_event_thread.db", kind=ChannelKind.EVENT)
        parent = DummyTextChannel()
        thread = DummyThread(parent)
        client = SimpleNamespace(get_channel=lambda cid: thread)
        captured: dict[str, object] = {}

        async def fake_webhook(**kwargs):
            captured.update(kwargs)
            return 123, None, [], None

        body = CreateEventBody(channelId="456", title="T", description="d")
        ctx = SimpleNamespace(guild=SimpleNamespace(id=1), roles=[])
        async with get_session() as db:
            original_dumps = json.dumps
            with patch(
                "demibot.http.routes.events.json.dumps",
                lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
            ), patch(
                "demibot.http.routes.events._send_via_webhook", fake_webhook
            ), patch(
                "demibot.http.routes.events.discord_client", client
            ), patch(
                "demibot.http.routes.events.discord.Thread", DummyThread
            ), patch(
                "demibot.http.routes.events.discord.TextChannel", DummyTextChannel
            ):
                await create_event(body=body, ctx=ctx, db=db)
        assert captured["channel"] is parent
        assert captured["thread"] is thread
    asyncio.run(run())
