import sys
from pathlib import Path
import types
import asyncio
import json
from types import SimpleNamespace
from datetime import datetime
from unittest.mock import patch
from sqlalchemy import select

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.db.models import Embed, Guild, GuildChannel, ChannelKind
from demibot.db.session import init_db, get_session
from demibot.http.routes.events import create_event, CreateEventBody


async def _run_test() -> None:
    db_path = Path("test_event_update.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)

    constant = datetime(2024, 1, 1)
    eid = str(int(constant.timestamp() * 1000))

    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test")
        db.add(guild)
        db.add(GuildChannel(guild_id=1, channel_id=123, kind=ChannelKind.EVENT))
        await db.commit()

    ctx = SimpleNamespace(guild=SimpleNamespace(id=1))
    body = CreateEventBody(
        channelId="123",
        title="First",
        time="2024-01-01T00:00:00Z",
        description="desc",
    )

    async with get_session() as db:
        original_dumps = json.dumps
        with patch(
            "demibot.http.routes.events.json.dumps",
            lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
        ), patch("demibot.http.routes.events.datetime") as dt:
            dt.utcnow.return_value = constant
            dt.fromisoformat.side_effect = datetime.fromisoformat
            await create_event(body=body, ctx=ctx, db=db)
        await db.commit()

    body2 = CreateEventBody(
        channelId="123",
        title="Second",
        time="2024-01-01T00:00:00Z",
        description="desc",
    )
    async with get_session() as db:
        original_dumps = json.dumps
        with patch(
            "demibot.http.routes.events.json.dumps",
            lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
        ), patch("demibot.http.routes.events.datetime") as dt:
            dt.utcnow.return_value = constant
            dt.fromisoformat.side_effect = datetime.fromisoformat
            await create_event(body=body2, ctx=ctx, db=db)
        row = await db.get(Embed, int(eid))
        payload = json.loads(row.payload_json)
        assert payload["title"] == "Second"
        res = await db.execute(select(Embed))
        embeds = res.scalars().all()
        assert len(embeds) == 1


def test_event_update_existing() -> None:
    asyncio.run(_run_test())

