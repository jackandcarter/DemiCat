import asyncio
import json
import sys
import types
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import pytest
from fastapi import HTTPException
from sqlalchemy import select

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.db.models import Guild, GuildChannel, ChannelKind, Embed
from demibot.db.session import init_db, get_session
from demibot.db import session as db_session
from demibot.http.routes.events import create_event, CreateEventBody


async def _run_ok() -> None:
    db_path = Path("test_event_buttons_ok.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    db_session._engine = None
    db_session._Session = None
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=123, kind=ChannelKind.EVENT))
        await db.commit()

    buttons = [
        {"label": f"b{i}", "customId": f"id{i}"} for i in range(25)
    ]
    body = CreateEventBody(
        channelId="123",
        title="Test",
        time="2024-01-01T00:00:00Z",
        description="desc",
        buttons=buttons,
    )
    ctx = SimpleNamespace(guild=SimpleNamespace(id=1))
    async with get_session() as db:
        original_dumps = json.dumps
        with patch(
            "demibot.http.routes.events.json.dumps",
            lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
        ):
            result = await create_event(body=body, ctx=ctx, db=db)
        row = (
            await db.execute(
                select(Embed).where(Embed.discord_message_id == int(result["id"]))
            )
        ).scalar_one()
        buttons_saved = json.loads(row.buttons_json)
        assert len(buttons_saved) == 25


def test_create_event_25_buttons() -> None:
    asyncio.run(_run_ok())


async def _run_fail() -> None:
    db_path = Path("test_event_buttons_fail.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    db_session._engine = None
    db_session._Session = None
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=123, kind=ChannelKind.EVENT))
        await db.commit()

    buttons = [
        {"label": f"b{i}", "customId": f"id{i}"} for i in range(26)
    ]
    body = CreateEventBody(
        channelId="123",
        title="Test",
        time="2024-01-01T00:00:00Z",
        description="desc",
        buttons=buttons,
    )
    ctx = SimpleNamespace(guild=SimpleNamespace(id=1))
    async with get_session() as db:
        original_dumps = json.dumps
        with patch(
            "demibot.http.routes.events.json.dumps",
            lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
        ):
            with pytest.raises(HTTPException) as exc:
                await create_event(body=body, ctx=ctx, db=db)
        assert exc.value.status_code == 422


def test_create_event_26_buttons() -> None:
    asyncio.run(_run_fail())
