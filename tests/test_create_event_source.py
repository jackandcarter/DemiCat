import sys
from pathlib import Path
import types
from types import SimpleNamespace
import asyncio
import json
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

from demibot.db.models import Embed, Guild, GuildChannel
from demibot.db.session import init_db, get_session
from demibot.http.routes.events import create_event, CreateEventBody


async def _run_test() -> None:
    db_path = Path("test_events.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=123, kind="event"))
        await db.commit()
        break

    body = CreateEventBody(
        channelId="123",
        title="Test Event",
        time="2024-01-01T00:00:00Z",
        description="desc",
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
        assert row.source == "demibot"
        break


def test_create_event_sets_source() -> None:
    asyncio.run(_run_test())
