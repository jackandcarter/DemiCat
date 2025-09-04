import sys
from pathlib import Path
import asyncio
import json
import sys
import types
from pathlib import Path
from types import SimpleNamespace
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

import discord
from demibot.db.session import init_db, get_session
from demibot.http.routes.events import create_event, CreateEventBody


class DummyChannel(discord.abc.Messageable):
    def __init__(self) -> None:
        self.last_content = None

    async def send(self, content=None, embed=None, view=None):
        self.last_content = content
        return SimpleNamespace(id=12345)

    async def _get_channel(self):
        return self


class DummyClient:
    def __init__(self) -> None:
        self.channel = DummyChannel()

    def get_channel(self, cid):
        return self.channel


async def _run_test() -> None:
    db_path = Path("test_event_mentions.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=123, kind=ChannelKind.EVENT))
        await db.commit()

    body = CreateEventBody(
        channelId="123",
        title="Test Event",
        time="2024-01-01T00:00:00Z",
        description="desc",
        mentions=["1", "2"],
    )
    ctx = SimpleNamespace(guild=SimpleNamespace(id=1))
    client = DummyClient()
    async with get_session() as db:
        original_dumps = json.dumps
        with patch("demibot.http.routes.events.json.dumps", lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k)):
            with patch("demibot.http.routes.events.discord_client", client):
                result = await create_event(body=body, ctx=ctx, db=db)
        row = (
            await db.execute(
                select(Embed).where(Embed.discord_message_id == int(result["id"]))
            )
        ).scalar_one()
        payload = json.loads(row.payload_json)
        assert payload["mentions"] == [1, 2]
        assert client.channel.last_content == "<@&1> <@&2>"


def test_create_event_mentions() -> None:
    asyncio.run(_run_test())

