from types import SimpleNamespace
import asyncio
import json
from pathlib import Path
from unittest.mock import patch

from sqlalchemy import select, text

root = Path(__file__).resolve().parents[1] / "demibot"
import sys, types
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.db.models import Guild, GuildChannel, User, ChannelKind, EventSignup
from demibot.db.session import init_db, get_session
from demibot.http.routes.events import (
    create_event,
    CreateEventBody,
    rsvp_event,
    RsvpBody,
)

async def _run_test():
    db_path = Path("test_rsvp_timestamp.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=10, discord_guild_id=10, name="Test Guild")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=555, kind=ChannelKind.EVENT))
        db.add(User(id=20, discord_user_id=20))
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
        ctx = SimpleNamespace(guild=SimpleNamespace(id=10))
        original_dumps = json.dumps
        with patch(
            "demibot.http.routes.events.json.dumps",
            lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
        ):
            result = await create_event(body=body, ctx=ctx, db=db)

        ctx_user = SimpleNamespace(user=SimpleNamespace(id=20), guild=SimpleNamespace(id=10))
        await rsvp_event(event_id=result["id"], body=RsvpBody(tag="join"), ctx=ctx_user, db=db)

        row = (
            await db.execute(
                select(EventSignup).where(
                    EventSignup.discord_message_id == int(result["id"]),
                    EventSignup.user_id == 20,
                )
            )
        ).scalar_one()
        first_time = row.created_at
        assert first_time is not None

        await rsvp_event(event_id=result["id"], body=RsvpBody(tag="maybe"), ctx=ctx_user, db=db)
        row2 = (
            await db.execute(
                select(EventSignup).where(
                    EventSignup.discord_message_id == int(result["id"]),
                    EventSignup.user_id == 20,
                )
            )
        ).scalar_one()
        assert row2.choice == "maybe"
        assert row2.created_at > first_time

        idx_rows = await db.execute(text("PRAGMA index_list('event_signups')"))
        index_names = [r[1] for r in idx_rows]
        assert "ix_event_signups_discord_message_id_choice" in index_names


def test_timestamp_and_index():
    asyncio.run(_run_test())
