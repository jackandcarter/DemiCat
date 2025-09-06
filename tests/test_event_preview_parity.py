import sys
from pathlib import Path
import types
from types import SimpleNamespace
import asyncio

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
from demibot.http.routes.events import create_event, list_events, CreateEventBody


async def _run_test() -> None:
    db_path = Path("test_preview.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=123, kind=ChannelKind.EVENT))
        await db.commit()

    embed = {"title": "Sample", "description": "Desc"}
    body = CreateEventBody(channelId="123", title="Sample", description="Desc", embeds=[embed])
    ctx = SimpleNamespace(guild=SimpleNamespace(id=1))
    async with get_session() as db:
        await create_event(body=body, ctx=ctx, db=db)
        events = await list_events(ctx=ctx, db=db)
        assert events[0]["embeds"][0] == embed


def test_preview_matches_server():
    asyncio.run(_run_test())
