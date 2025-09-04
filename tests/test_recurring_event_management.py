from pathlib import Path
import sys
import asyncio
import sys
from datetime import datetime, timedelta
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
import types

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.db.models import Guild, GuildChannel, RecurringEvent, ChannelKind
from demibot.db.session import init_db, get_session
from demibot.http.routes.events import (
    create_event,
    CreateEventBody,
    RepeatPatchBody,
    update_recurring_event,
    delete_recurring_event,
)
from demibot import repeat_events
from demibot.repeat_events import process_recurring_events_once


async def _setup_db(path: Path) -> None:
    if path.exists():
        path.unlink()
    url = f"sqlite+aiosqlite:///{path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=123, kind=ChannelKind.EVENT))
        await db.commit()


async def _test_patch_delete() -> None:
    db_path = Path("test_recurring_api.db")
    await _setup_db(db_path)

    body = CreateEventBody(
        channelId="123",
        title="Test",
        time="2024-01-01T00:00:00Z",
        description="desc",
        repeat="daily",
    )
    ctx = SimpleNamespace(guild=SimpleNamespace(id=1))
    async with get_session() as db:
        res = await create_event(body=body, ctx=ctx, db=db)
        ev_id = int(res["id"])
        await db.commit()

    new_time = (datetime.utcnow() + timedelta(days=5)).strftime("%Y-%m-%dT%H:%M:%S.%fZ")
    patch_body = RepeatPatchBody(time=new_time, repeat="weekly")
    async with get_session() as db:
        await update_recurring_event(str(ev_id), patch_body, ctx=ctx, db=db)
        row = await db.get(RecurringEvent, ev_id)
        assert row.repeat == "weekly"
        assert row.next_post_at.strftime("%Y-%m-%dT%H:%M:%S.%fZ") == new_time
        await delete_recurring_event(str(ev_id), ctx=ctx, db=db)
        row = await db.get(RecurringEvent, ev_id)
        assert row is None


async def _test_cleanup() -> None:
    db_path = Path("test_recurring_cleanup.db")
    await _setup_db(db_path)

    async with get_session() as db:
        db.add(
            RecurringEvent(
                id=111,
                guild_id=1,
                channel_id=123,
                repeat="daily",
                next_post_at=datetime.utcnow() + timedelta(days=1),
                payload_json="{}",
            )
        )
        await db.commit()

    repeat_events.discord_client = SimpleNamespace(get_channel=lambda _: None)
    await process_recurring_events_once()
    async with get_session() as db:
        assert (await db.get(RecurringEvent, 111)) is None

    async with get_session() as db:
        db.add(
            RecurringEvent(
                id=222,
                guild_id=1,
                channel_id=123,
                repeat="daily",
                next_post_at=datetime.utcnow() + timedelta(days=1),
                payload_json="{}",
            )
        )
        await db.commit()

    class DummyChannel:
        async def fetch_message(self, _):
            raise Exception()

    repeat_events.discord_client = SimpleNamespace(get_channel=lambda _: DummyChannel())
    await process_recurring_events_once()
    async with get_session() as db:
        assert (await db.get(RecurringEvent, 222)) is None

    repeat_events.discord_client = None


def test_recurring_event_patch_delete() -> None:
    asyncio.run(_test_patch_delete())


def test_recurring_event_cleanup() -> None:
    asyncio.run(_test_cleanup())
