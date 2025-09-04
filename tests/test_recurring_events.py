import sys
from pathlib import Path
import types
import asyncio
import json
from types import SimpleNamespace
from datetime import datetime, timedelta
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

from demibot.db.models import Embed, Guild, GuildChannel, RecurringEvent, ChannelKind
from demibot.db.session import init_db, get_session
from demibot.http.routes.events import create_event, CreateEventBody
from demibot.repeat_events import process_recurring_events_once


async def _run_test() -> None:
    db_path = Path("test_recurring.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=123, kind=ChannelKind.EVENT))
        await db.commit()
        break

    body = CreateEventBody(
        channelId="123",
        title="Test Event",
        time="2024-01-01T00:00:00Z",
        description="desc",
        repeat="daily",
    )
    ctx = SimpleNamespace(guild=SimpleNamespace(id=1))
    async with get_session() as db:
        original_dumps = json.dumps
        with patch(
            "demibot.http.routes.events.json.dumps",
            lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
        ):
            await create_event(body=body, ctx=ctx, db=db)
        row = (await db.execute(select(RecurringEvent))).scalar_one()
        row.next_post_at = datetime.utcnow() - timedelta(seconds=1)
        await db.commit()
        break

    await process_recurring_events_once()

    async with get_session() as db:
        embeds = (await db.execute(select(Embed))).scalars().all()
        assert len(embeds) == 2
        row = (await db.execute(select(RecurringEvent))).scalar_one()
        assert row.next_post_at > datetime.utcnow()
        break


def test_recurring_event_reposted() -> None:
    asyncio.run(_run_test())
