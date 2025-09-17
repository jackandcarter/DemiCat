from __future__ import annotations

from types import SimpleNamespace
from datetime import datetime
import asyncio
import os
import sys
import types
from pathlib import Path

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

from demibot.discordbot.cogs import mirror as mirror_mod
from demibot.discordbot.cogs.mirror import Mirror
from demibot.db.models import Guild, GuildChannel, Message, ChannelKind, User
from demibot.db.session import get_session, init_db
from sqlalchemy import select, text


def _fake_serialize(message):
    class DTO:
        author_name = message.author.display_name
        author_avatar_url = None

        def model_dump(self, *args, **kwargs):
            return {
                "id": str(message.id),
                "channelId": str(message.channel.id),
                "authorName": self.author_name,
                "authorAvatarUrl": None,
                "content": message.content,
            }

    fragments = {
        "attachments_json": None,
        "mentions_json": None,
        "author_json": "{}",
        "embeds_json": None,
        "reference_json": None,
        "components_json": None,
        "reactions_json": None,
    }
    return DTO(), fragments


mirror_mod.serialize_message = _fake_serialize


class DummyBot:
    def __init__(self, url: str) -> None:
        self.cfg = SimpleNamespace(database=SimpleNamespace(url=url))


class DummyAuthor:
    def __init__(self, bot: bool, id: int) -> None:
        self.bot = bot
        self.id = id
        self.display_name = f"bot{id}"
        self.name = f"bot{id}"
        self.global_name = f"Global{id}"
        self.discriminator = "1234"
        self.display_avatar = SimpleNamespace(url=f"https://example.com/{id}.png")


class DummyChannel:
    def __init__(self, cid: int) -> None:
        self.id = cid


class DummyMessage:
    def __init__(self, channel_id: int, author: DummyAuthor, content: str) -> None:
        self.author = author
        self.channel = DummyChannel(channel_id)
        self.id = 42
        self.content = content
        self.mentions: list[object] = []
        self.attachments: list[object] = []
        self.embeds: list[object] = []
        self.reference = None
        self.components: list[object] = []
        self.reactions: list[object] = []
        self.created_at = datetime.utcnow()
        self.edited_at = None


async def _prepare() -> None:
    await init_db("sqlite+aiosqlite://")
    async with get_session() as db:
        await db.execute(text("DELETE FROM messages"))
        await db.execute(text("DELETE FROM users"))
        await db.execute(text("DELETE FROM guild_channels"))
        await db.execute(text("DELETE FROM guilds"))
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=123, kind=ChannelKind.CHAT))
        await db.commit()


async def _send_message(whitelist_env: str | None) -> bool:
    if whitelist_env is None:
        os.environ.pop("BOT_MIRROR_WHITELIST", None)
    else:
        os.environ["BOT_MIRROR_WHITELIST"] = whitelist_env

    await _prepare()
    bot = DummyBot("sqlite+aiosqlite://")
    mirror = Mirror(bot)
    author = DummyAuthor(bot=True, id=999)
    msg = DummyMessage(123, author, "hi")
    await mirror.on_message(msg)
    async with get_session() as db:
        return await db.get(Message, msg.id) is not None


def test_unlisted_bot_ignored() -> None:
    assert asyncio.run(_send_message(None)) is False


def test_whitelisted_bot_persisted() -> None:
    assert asyncio.run(_send_message("999")) is True


def test_new_user_is_created_and_linked() -> None:
    async def _run() -> None:
        await _prepare()
        bot = DummyBot("sqlite+aiosqlite://")
        mirror = Mirror(bot)
        author = DummyAuthor(bot=False, id=321)
        msg = DummyMessage(123, author, "hi")
        await mirror.on_message(msg)
        async with get_session() as db:
            user = await db.scalar(
                select(User).where(User.discord_user_id == author.id)
            )
            stored = await db.get(Message, msg.id)
            assert user is not None
            assert stored is not None
            assert stored.author_id == user.id

    asyncio.run(_run())

