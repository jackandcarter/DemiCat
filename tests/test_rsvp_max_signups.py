import sys
from pathlib import Path
import types
from types import SimpleNamespace
import asyncio
import json
from fastapi.responses import JSONResponse

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.db.models import Guild, GuildChannel, User, ChannelKind
from demibot.db.session import init_db, get_session
from demibot.http.routes.events import (
    create_event,
    CreateEventBody,
    rsvp_event,
    RsvpBody,
    list_attendees,
)
from demibot.http.routes.interactions import post_interaction, InteractionBody
from unittest.mock import patch


async def _run_test() -> None:
    db_path = Path("test_rsvp.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=123, kind=ChannelKind.EVENT))
        db.add(User(id=1, discord_user_id=1))
        db.add(User(id=2, discord_user_id=2))
        await db.commit()

        body = CreateEventBody(
            channelId="123",
            title="Test",
            time="2024-01-01T00:00:00Z",
            description="desc",
            buttons=[{"label": "Join", "customId": "rsvp:join", "maxSignups": 1}],
        )
        ctx = SimpleNamespace(guild=SimpleNamespace(id=1))
        original_dumps = json.dumps
        with patch(
            "demibot.http.routes.events.json.dumps",
            lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
        ):
            result = await create_event(body=body, ctx=ctx, db=db)

        ctx1 = SimpleNamespace(user=SimpleNamespace(id=1), guild=SimpleNamespace(id=1))
        await post_interaction(
            body=InteractionBody(messageId=result["id"], customId="rsvp:join"),
            ctx=ctx1,
            db=db,
        )

        ctx2 = SimpleNamespace(user=SimpleNamespace(id=2), guild=SimpleNamespace(id=1))
        resp = await post_interaction(
            body=InteractionBody(messageId=result["id"], customId="rsvp:join"),
            ctx=ctx2,
            db=db,
        )
        assert isinstance(resp, JSONResponse)
        assert resp.status_code == 400


def test_max_signups_enforced() -> None:
    asyncio.run(_run_test())


async def _run_rest_test() -> None:
    db_path = Path("test_rsvp_rest.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=2, discord_guild_id=2, name="Test Guild")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=124, kind=ChannelKind.EVENT))
        db.add(User(id=3, discord_user_id=3))
        db.add(User(id=4, discord_user_id=4))
        await db.commit()

        body = CreateEventBody(
            channelId="124",
            title="Test",
            time="2024-01-01T00:00:00Z",
            description="desc",
            buttons=[{"label": "Join", "customId": "rsvp:join", "maxSignups": 1}],
        )
        ctx = SimpleNamespace(guild=SimpleNamespace(id=2))
        original_dumps = json.dumps
        with patch(
            "demibot.http.routes.events.json.dumps",
            lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
        ):
            result = await create_event(body=body, ctx=ctx, db=db)

        ctx1 = SimpleNamespace(user=SimpleNamespace(id=3), guild=SimpleNamespace(id=2))
        await rsvp_event(event_id=result["id"], body=RsvpBody(tag="join"), ctx=ctx1, db=db)

        ctx2 = SimpleNamespace(user=SimpleNamespace(id=4), guild=SimpleNamespace(id=2))
        resp = await rsvp_event(event_id=result["id"], body=RsvpBody(tag="join"), ctx=ctx2, db=db)
        assert isinstance(resp, JSONResponse)
        assert resp.status_code == 400

        attendees = await list_attendees(event_id=result["id"], ctx=ctx1, db=db)
        assert attendees == [{"tag": "join", "userId": 3}]


def test_rest_rsvp_and_attendees() -> None:
    asyncio.run(_run_rest_test())
