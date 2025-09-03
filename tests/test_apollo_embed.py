from types import SimpleNamespace

import sys
from pathlib import Path
import types

import asyncio
import discord
from sqlalchemy import select

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

discordbot_pkg = types.ModuleType("demibot.discordbot")
discordbot_pkg.__path__ = [str(root / "demibot/discordbot")]
sys.modules.setdefault("demibot.discordbot", discordbot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.discordbot.cogs.mirror import Mirror
from demibot.db.models import Embed, Guild, GuildChannel
from demibot.db.session import get_session, init_db
from demibot.http.routes.embeds import get_embeds


class DummyBot:
    def __init__(self, url: str) -> None:
        self.cfg = SimpleNamespace(database=SimpleNamespace(url=url))


class DummyAuthor:
    def __init__(self) -> None:
        self.bot = True
        self.id = 999
        self.display_name = "Apollo"
        self.name = "Apollo"


class DummyChannel:
    def __init__(self, cid: int) -> None:
        self.id = cid


class DummyMessage:
    def __init__(self, channel_id: int, embed: discord.Embed) -> None:
        self.author = DummyAuthor()
        self.channel = DummyChannel(channel_id)
        self.id = 42
        self.content = ""
        self.mentions = []
        self.embeds = [embed]
        btn = SimpleNamespace(
            type=2,
            custom_id="rsvp:yes",
            label="Yes",
            style=1,
            emoji=None,
        )
        row = SimpleNamespace(children=[btn])
        self.components = [row]
async def _run_test() -> None:
    db_path = Path("test.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    bot = DummyBot(url)
    Mirror(bot)  # initializes DB

    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=123, kind="event"))
        await db.commit()
        break

    emb = discord.Embed(title="Raid Night", description="Prepare")
    emb.set_footer(text="Powered by Apollo")
    msg = DummyMessage(123, emb)

    mirror = Mirror(bot)
    await mirror.on_message(msg)

    async with get_session() as db:
        row = (
            await db.execute(
                select(Embed).where(Embed.discord_message_id == msg.id)
            )
        ).scalar_one()
        assert row.source == "apollo"
        assert row.buttons_json is not None
        ctx = SimpleNamespace(guild=SimpleNamespace(id=1), roles=["officer"])
        result = await get_embeds(ctx=ctx, db=db, source="apollo", channel_id=123)
        assert result and result[0]["id"] == str(msg.id)
        assert result[0]["guildId"] == 1
        assert result[0]["channelId"] == 123
        assert result[0]["buttons"][0]["customId"] == "rsvp:yes"
        break


def test_apollo_embed_stored_and_retrieved() -> None:
    asyncio.run(_run_test())
