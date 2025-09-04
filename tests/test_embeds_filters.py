import sys
import json
import asyncio
from pathlib import Path
from types import SimpleNamespace
import types

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

from demibot.db.models import Embed, Guild, GuildChannel, ChannelKind
from demibot.db.session import init_db, get_session
from demibot.http.routes.embeds import get_embeds

async def _run_test() -> None:
    db_path = Path("test_embeds_filters.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=10, kind=ChannelKind.EVENT))
        db.add(GuildChannel(guild_id=guild.id, channel_id=20, kind=ChannelKind.EVENT))
        db.add(Embed(discord_message_id=1, channel_id=10, guild_id=1, payload_json=json.dumps({"id": "1"}), buttons_json=None, source="test"))
        db.add(Embed(discord_message_id=2, channel_id=10, guild_id=1, payload_json=json.dumps({"id": "2"}), buttons_json=None, source="test"))
        db.add(Embed(discord_message_id=3, channel_id=20, guild_id=1, payload_json=json.dumps({"id": "3"}), buttons_json=None, source="test"))
        await db.commit()

    ctx = SimpleNamespace(guild=SimpleNamespace(id=1), roles=["officer"])
    async with get_session() as db:
        res = await get_embeds(ctx=ctx, db=db, channel_id=10)
        assert len(res) == 2
        assert all(e["channelId"] == 10 for e in res)
        res2 = await get_embeds(ctx=ctx, db=db, limit=1)
        assert len(res2) == 1
        res3 = await get_embeds(ctx=ctx, db=db, channel_id=10, limit=1)
        assert len(res3) == 1 and res3[0]["channelId"] == 10

def test_embeds_filters() -> None:
    asyncio.run(_run_test())
