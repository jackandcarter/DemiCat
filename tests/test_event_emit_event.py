import sys
from pathlib import Path
import types
import asyncio
import json
from types import SimpleNamespace
from unittest.mock import patch
from datetime import datetime

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.db.models import Guild, GuildChannel, ChannelKind, User
from demibot.db.session import init_db, get_session
from demibot.http.routes.events import (
    create_event,
    rsvp_event,
    delete_recurring_event,
    CreateEventBody,
    RsvpBody,
)


async def _run_test() -> None:
    db_path = Path("test_event_emit_event.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=9999, discord_guild_id=9999, name="Test")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=9999, kind=ChannelKind.EVENT))
        db.add(User(id=9991, discord_user_id=9991))
        await db.commit()

        ctx = SimpleNamespace(guild=SimpleNamespace(id=guild.id))
        body = CreateEventBody(
            channelId="9999",
            title="Title",
            time="2024-01-01T00:00:00Z",
            description="desc",
            repeat="daily",
        )

        events: list[dict] = []

        async def dummy_emit(ev):
            events.append(ev)

        async def dummy_broadcast(*a, **k):
            pass

        constant = datetime(2024, 1, 1)
        original_dumps = json.dumps
        with patch(
            "demibot.http.routes.events.json.dumps",
            lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
        ), patch("demibot.http.routes.events.datetime") as dt, patch(
            "demibot.http.routes.events.manager.broadcast_text", dummy_broadcast
        ), patch("demibot.http.routes.events.emit_event", dummy_emit):
            dt.utcnow.return_value = constant
            dt.now.return_value = constant
            dt.fromisoformat.side_effect = datetime.fromisoformat
            res = await create_event(body=body, ctx=ctx, db=db)
        assert events and events[0]["op"] == "ec" and events[0]["channel"] == 9999
        event_id = res["id"]

        ctx_user = SimpleNamespace(user=SimpleNamespace(id=9991), guild=SimpleNamespace(id=guild.id))
        events.clear()
        with patch(
            "demibot.http.routes.events.manager.broadcast_text", dummy_broadcast
        ), patch("demibot.http.routes.events.emit_event", dummy_emit):
            await rsvp_event(event_id=event_id, body=RsvpBody(tag="yes"), ctx=ctx_user, db=db)
        assert events and events[0]["op"] == "eu" and events[0]["channel"] == 9999

        events.clear()
        with patch("demibot.http.routes.events.emit_event", dummy_emit):
            await delete_recurring_event(event_id=event_id, ctx=ctx, db=db)
        assert events and events[0]["op"] == "ed" and events[0]["channel"] == 9999
        assert events[0]["d"]["id"] == event_id


def test_emit_event_called() -> None:
    asyncio.run(_run_test())
