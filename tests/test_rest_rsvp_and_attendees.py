import sys
from pathlib import Path
import types
from types import SimpleNamespace
import asyncio
import json
from unittest.mock import patch

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


async def _run_test() -> None:
    db_path = Path("test_rest_rsvp_and_attendees.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=10, discord_guild_id=10, name="Test Guild")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=555, kind=ChannelKind.EVENT))
        db.add(User(id=20, discord_user_id=20))
        db.add(User(id=21, discord_user_id=21))
        db.add(User(id=22, discord_user_id=22))
        await db.commit()

        body = CreateEventBody(
            channelId="555",
            title="Test",
            time="2024-01-01T00:00:00Z",
            description="desc",
            buttons=[
                {"label": "Join", "customId": "rsvp:join"},
                {"label": "Maybe", "customId": "rsvp:maybe"},
            ],
        )
        ctx = SimpleNamespace(guild=SimpleNamespace(id=guild.id))
        original_dumps = json.dumps
        with patch(
            "demibot.http.routes.events.json.dumps",
            lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
        ):
            result = await create_event(body=body, ctx=ctx, db=db)

        ctx1 = SimpleNamespace(user=SimpleNamespace(id=20), guild=SimpleNamespace(id=guild.id))
        await rsvp_event(event_id=result["id"], body=RsvpBody(tag="join"), ctx=ctx1, db=db)

        ctx2 = SimpleNamespace(user=SimpleNamespace(id=21), guild=SimpleNamespace(id=guild.id))
        await rsvp_event(event_id=result["id"], body=RsvpBody(tag="maybe"), ctx=ctx2, db=db)

        ctx3 = SimpleNamespace(user=SimpleNamespace(id=22), guild=SimpleNamespace(id=guild.id))
        await rsvp_event(event_id=result["id"], body=RsvpBody(tag="join"), ctx=ctx3, db=db)

        attendees = await list_attendees(event_id=result["id"], ctx=ctx1, db=db)
        assert attendees == [
            {"tag": "join", "userId": 20},
            {"tag": "join", "userId": 22},
            {"tag": "maybe", "userId": 21},
        ]


def test_rest_rsvp_and_attendees() -> None:
    asyncio.run(_run_test())
