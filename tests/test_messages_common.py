import asyncio
import types
from typing import List, Tuple
from datetime import datetime
import json

import pytest
from fastapi import HTTPException
from sqlalchemy import select, text

from demibot.db.models import Guild, User, Message, Membership, GuildChannel, ChannelKind
from demibot.db.session import init_db, get_session
from demibot.http.deps import RequestContext
import importlib.util
import pathlib
import demibot.http.schemas  # ensure pydantic models are registered
import sys
import types

sys.modules.setdefault("demibot.http.routes", types.ModuleType("demibot.http.routes"))

spec = importlib.util.spec_from_file_location(
    "demibot.http.routes._messages_common",
    pathlib.Path("demibot/demibot/http/routes/_messages_common.py"),
)
mc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mc)  # type: ignore
mc.PostBody.model_rebuild(
    _types_namespace={"MessageReferenceDto": mc.MessageReferenceDto}
)
mc.ChatMessage.model_config["populate_by_name"] = True
mc.MessageAuthor.model_config["populate_by_name"] = True
mc.ChatMessage.model_rebuild(
    _types_namespace={
        "AttachmentDto": mc.AttachmentDto,
        "Mention": mc.Mention,
        "MessageAuthor": mc.MessageAuthor,
        "EmbedDto": mc.EmbedDto,
        "MessageReferenceDto": mc.MessageReferenceDto,
        "ButtonComponentDto": mc.ButtonComponentDto,
        "ReactionDto": mc.ReactionDto,
    }
)


class DummyKey:
    pass


def test_save_and_fetch_messages(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=1, discord_guild_id=1, name="Guild"))
            db.add(User(id=1, discord_user_id=10, global_name="Alice"))
            db.add(
                Membership(
                    guild_id=1,
                    user_id=1,
                    nickname="AliceNick",
                    avatar_url="http://example.com/avatar.png",
                )
            )
            db.add(GuildChannel(guild_id=1, channel_id=123, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 1)
            user = await db.get(User, 1)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            calls: List[Tuple[str, int, bool, str | None]] = []

            async def dummy_broadcast(message: str, guild_id: int, officer_only: bool = False, path: str | None = None):
                calls.append((message, guild_id, officer_only, path))

            monkeypatch.setattr(mc.manager, "broadcast_text", dummy_broadcast)

            class DummyWebhook:
                url = "http://example.com"

                async def send(self, *args, **kwargs):
                    return types.SimpleNamespace(id=1, attachments=[])

            class DummyChannel:
                async def create_webhook(self, name: str):
                    return DummyWebhook()

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyChannel()

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord.abc, "Messageable", DummyChannel)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="123", content="hello")
            res = await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert res["ok"] is True

            msg = (await db.execute(select(Message))).scalar_one()
            assert msg.is_officer is False
            assert msg.author_name == "AliceNick"
            assert msg.author_avatar_url == "http://example.com/avatar.png"

            assert calls and calls[0][2] is False and calls[0][3] == "/ws/messages"

            data = await mc.fetch_messages("123", ctx, db, is_officer=False)
            assert len(data) == 1 and data[0]["content"] == "hello"
            assert data[0]["authorName"] == "AliceNick"
            assert data[0]["authorAvatarUrl"] == "http://example.com/avatar.png"

    asyncio.run(_run())


def test_fetch_messages_backfills_from_discord(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            db.add(Guild(id=1, discord_guild_id=1, name="Guild"))
            db.add(User(id=1, discord_user_id=1, global_name="Bob"))
            await db.commit()
            guild = await db.get(Guild, 1)
            user = await db.get(User, 1)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            msgs = [
                types.SimpleNamespace(
                    id=1,
                    channel=types.SimpleNamespace(id=123),
                    author=types.SimpleNamespace(id=1, display_name="Bob"),
                    created_at=datetime(2024, 1, 1),
                    content="first",
                    attachments=[],
                    mentions=[],
                    role_mentions=[],
                    channel_mentions=[],
                    embeds=[],
                    reference=None,
                    components=[],
                    reactions=[],
                    edited_at=None,
                ),
                types.SimpleNamespace(
                    id=2,
                    channel=types.SimpleNamespace(id=123),
                    author=types.SimpleNamespace(id=1, display_name="Bob"),
                    created_at=datetime(2024, 1, 2),
                    content="second",
                    attachments=[],
                    mentions=[],
                    role_mentions=[],
                    channel_mentions=[],
                    embeds=[],
                    reference=None,
                    components=[],
                    reactions=[],
                    edited_at=None,
                ),
            ]

            def fake_serialize_message(message):
                dto = types.SimpleNamespace(
                    id=str(message.id),
                    channel_id=str(message.channel.id),
                    author_name="Bob",
                    author_avatar_url=None,
                    timestamp=message.created_at,
                    content=message.content,
                    attachments=None,
                    mentions=None,
                    author=None,
                    embeds=None,
                    reference=None,
                    components=None,
                    reactions=None,
                    edited_timestamp=None,
                )
                fragments = {
                    "attachments_json": None,
                    "mentions_json": None,
                    "author_json": "{}",
                    "embeds_json": None,
                    "reference_json": None,
                    "components_json": None,
                    "reactions_json": None,
                }
                return dto, fragments

            monkeypatch.setattr(mc, "serialize_message", fake_serialize_message)

            class DummyChannel:
                def __init__(self, messages):
                    self.messages = messages
                    self.calls: list[int | None] = []

                def history(self, limit=None):
                    self.calls.append(limit)

                    async def gen():
                        for m in self.messages[: limit or len(self.messages)]:
                            yield m

                    return gen()

            dummy_channel = DummyChannel(msgs)

            class DummyClient:
                def get_channel(self, cid: int):
                    return dummy_channel

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord.abc, "Messageable", DummyChannel)

            data = await mc.fetch_messages("123", ctx, db, is_officer=False, limit=5)
            assert dummy_channel.calls == [5]
            assert [m["content"] for m in data] == ["first", "second"]

            rows = (await db.execute(select(Message))).scalars().all()
            assert len(rows) == 2

    asyncio.run(_run())


def test_channel_not_configured(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=99, discord_guild_id=99, name="Guild"))
            db.add(User(id=99, discord_user_id=990, global_name="Alice"))
            await db.commit()
            guild = await db.get(Guild, 99)
            user = await db.get(User, 99)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])
            body = mc.PostBody(channelId="123", content="hi")
            with pytest.raises(HTTPException) as exc:
                await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert exc.value.status_code == 400
            assert "channel not configured" in str(exc.value.detail)
    asyncio.run(_run())


def test_forum_root_rejected(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=8, discord_guild_id=8, name="Guild"))
            db.add(User(id=8, discord_user_id=80, global_name="Alice"))
            db.add(GuildChannel(guild_id=8, channel_id=1, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 8)
            user = await db.get(User, 8)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            class DummyForum:
                pass

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyForum()

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord, "ForumChannel", DummyForum)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="1", content="hi")
            with pytest.raises(HTTPException) as exc:
                await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert exc.value.status_code == 400

    asyncio.run(_run())


def test_thread_uses_parent_webhook(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=11, discord_guild_id=11, name="Guild"))
            db.add(User(id=11, discord_user_id=110, global_name="Alice"))
            db.add(GuildChannel(guild_id=11, channel_id=456, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 11)
            user = await db.get(User, 11)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            captured: dict[str, object] = {}

            class DummyWebhook:
                url = "http://example.com"

                async def send(self, *args, **kwargs):
                    captured["thread"] = kwargs.get("thread")
                    return types.SimpleNamespace(id=1, attachments=[])

            class DummyParent:
                id = 123

                async def create_webhook(self, name: str):
                    captured["created_on_parent"] = True
                    return DummyWebhook()

            class DummyThread(DummyParent):
                id = 456
                parent = DummyParent()

                async def create_webhook(self, name: str):
                    captured["created_on_thread"] = True
                    return DummyWebhook()

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyThread()

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord.abc, "Messageable", DummyParent)
            monkeypatch.setattr(mc.discord, "Thread", DummyThread)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="456", content="hello")
            res = await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert res["ok"] is True
            assert isinstance(captured.get("thread"), DummyThread)
            assert captured.get("created_on_parent") is True
            assert "created_on_thread" not in captured

    asyncio.run(_run())


def test_archived_thread_rejected(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=13, discord_guild_id=13, name="Guild"))
            db.add(User(id=13, discord_user_id=130, global_name="Alice"))
            db.add(GuildChannel(guild_id=13, channel_id=456, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 13)
            user = await db.get(User, 13)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            class DummyThread:
                archived = True

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyThread()

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord, "Thread", DummyThread)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="456", content="hello")
            with pytest.raises(HTTPException) as exc:
                await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert exc.value.status_code == 400
            assert exc.value.detail == "thread is archived"

    asyncio.run(_run())


def test_cached_webhook_without_channel(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=12, discord_guild_id=12, name="Guild"))
            db.add(User(id=12, discord_user_id=120, global_name="Alice"))
            db.add(GuildChannel(guild_id=12, channel_id=123, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 12)
            user = await db.get(User, 12)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            captured: dict[str, object] = {}

            class DummyWebhook:
                async def send(self, *args, **kwargs):
                    captured["sent"] = True
                    return types.SimpleNamespace(id=1, attachments=[])

            class DummyWebhookCls:
                @staticmethod
                def from_url(url: str, client=None):
                    return DummyWebhook()

            class DummyClient:
                def get_channel(self, cid: int):
                    return None

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord, "Webhook", DummyWebhookCls)
            monkeypatch.setattr(mc, "_channel_webhooks", {123: "http://example.com"})

            body = mc.PostBody(channelId="123", content="hello")
            res = await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert res["ok"] is True
            assert captured.get("sent") is True

    asyncio.run(_run())



def test_allowed_mentions(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=5, discord_guild_id=5, name="Guild"))
            db.add(User(id=5, discord_user_id=50, global_name="Charlie"))
            db.add(
                Membership(
                    guild_id=5,
                    user_id=5,
                    nickname="CharlieNick",
                    avatar_url="http://example.com/avatar.png",
                )
            )
            db.add(GuildChannel(guild_id=5, channel_id=123, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 5)
            user = await db.get(User, 5)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            captured: dict[str, object] = {}

            class DummyWebhook:
                url = "http://example.com"

                async def send(self, *args, **kwargs):
                    captured["allowed_mentions"] = kwargs.get("allowed_mentions")
                    return types.SimpleNamespace(id=1, attachments=[])

            class DummyChannel:
                async def create_webhook(self, name: str):
                    return DummyWebhook()

                async def send(self, *args, **kwargs):
                    captured["allowed_mentions"] = kwargs.get("allowed_mentions")
                    return types.SimpleNamespace(id=1, attachments=[])

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyChannel()

            async def dummy_broadcast(
                message: str,
                guild_id: int,
                officer_only: bool = False,
                path: str | None = None,
            ):
                pass

            monkeypatch.setattr(mc.manager, "broadcast_text", dummy_broadcast)
            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord.abc, "Messageable", DummyChannel)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="123", content="@everyone hello @here")
            res = await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert res["ok"] is True

            am = captured.get("allowed_mentions")
            assert isinstance(am, mc.discord.AllowedMentions)
            assert am.everyone is False
            assert am.roles is True
            assert am.users is True

    asyncio.run(_run())


def test_long_username_truncated(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            long_name = "A" * 100
            db.add(Guild(id=3, discord_guild_id=3, name="Guild"))
            db.add(User(id=3, discord_user_id=30, global_name="Alice"))
            db.add(
                Membership(
                    guild_id=3,
                    user_id=3,
                    nickname=long_name,
                    avatar_url="http://example.com/avatar.png",
                )
            )
            db.add(GuildChannel(guild_id=3, channel_id=123, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 3)
            user = await db.get(User, 3)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            async def dummy_broadcast(
                message: str, guild_id: int, officer_only: bool = False, path: str | None = None
            ):
                pass

            monkeypatch.setattr(mc.manager, "broadcast_text", dummy_broadcast)

            captured: dict[str, str] = {}

            class DummyWebhook:
                url = "http://example.com"

                async def send(self, *args, **kwargs):
                    captured["username"] = kwargs.get("username")
                    return types.SimpleNamespace(id=1, attachments=[])

            class DummyChannel:
                async def create_webhook(self, name: str):
                    return DummyWebhook()

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyChannel()

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord.abc, "Messageable", DummyChannel)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="123", content="hello")
            res = await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert res["ok"] is True
            assert len(captured["username"]) == 80

    asyncio.run(_run())


def test_message_too_long(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=42, discord_guild_id=42, name="Guild"))
            db.add(User(id=42, discord_user_id=420, global_name="Alice"))
            db.add(GuildChannel(guild_id=42, channel_id=123, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 42)
            user = await db.get(User, 42)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])
            body = mc.PostBody(channelId="123", content="x" * 2001)
            with pytest.raises(HTTPException) as exc:
                await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert exc.value.status_code == 400
            assert (
                exc.value.detail
                == "Message too long (max 2000 characters)."
            )

    asyncio.run(_run())


def test_rest_ws_payload_parity(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=2, discord_guild_id=2, name="Guild"))
            db.add(User(id=2, discord_user_id=20, global_name="Bob"))
            db.add(
                Membership(
                    guild_id=2,
                    user_id=2,
                    nickname="BobNick",
                    avatar_url="http://example.com/avatar.png",
                )
            )
            db.add(GuildChannel(guild_id=2, channel_id=123, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 2)
            user = await db.get(User, 2)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            async def dummy_broadcast(message: str, guild_id: int, officer_only: bool = False, path: str | None = None):
                pass

            monkeypatch.setattr(mc.manager, "broadcast_text", dummy_broadcast)

            class DummyWebhook:
                url = "http://example.com"

                async def send(self, *args, **kwargs):
                    return types.SimpleNamespace(id=1, attachments=[])

            class DummyChannel:
                async def create_webhook(self, name: str):
                    return DummyWebhook()

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyChannel()

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord.abc, "Messageable", DummyChannel)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="123", content="hello")
            await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            rest_data = await mc.fetch_messages("123", ctx, db, is_officer=False)
            assert len(rest_data) == 1
            rest_msg = rest_data[0]
            assert "channelId" in rest_msg and "channel_id" not in rest_msg

            import demibot.http.ws_chat as wc

            manager = wc.ChatConnectionManager()

            async def fake_flush(self, channel: str) -> None:
                return None

            monkeypatch.setattr(wc.ChatConnectionManager, "_flush_channel", fake_flush)

            await manager.send("123", {"op": "mc", "d": rest_msg})
            ws_msg = manager._channel_queues["123"][0]
            assert ws_msg["d"] == rest_msg
            assert "cursor" in ws_msg

    asyncio.run(_run())

def test_multipart_message(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=9, discord_guild_id=9, name="Guild"))
            db.add(User(id=9, discord_user_id=90, global_name="Alice"))
            db.add(GuildChannel(guild_id=9, channel_id=123, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 9)
            user = await db.get(User, 9)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            async def dummy_broadcast(message: str, guild_id: int, officer_only: bool = False, path: str | None = None):
                pass

            monkeypatch.setattr(mc.manager, "broadcast_text", dummy_broadcast)
            monkeypatch.setattr(mc.AttachmentDto, "contentType", property(lambda self: self.content_type), raising=False)

            def fake_serialize_message(message):
                dto = types.SimpleNamespace(
                    id=str(message.id),
                    channel_id=str(message.channel.id),
                    author_name="Alice",
                    author_avatar_url=None,
                    timestamp=datetime.utcnow(),
                    content=message.content,
                    attachments=[
                        types.SimpleNamespace(
                            url=a.url,
                            filename=a.filename,
                            contentType=a.content_type,
                        )
                        for a in message.attachments
                    ],
                    mentions=[],
                    author=None,
                    embeds=[],
                    reference=None,
                    components=[],
                    reactions=[],
                    edited_timestamp=None,
                    model_dump=lambda **kwargs: {},
                    model_dump_json=lambda **kwargs: "{}",
                )
                fragments = {
                    "author_json": "{}",
                    "attachments_json": json.dumps(
                        [
                            {
                                "url": a.url,
                                "filename": a.filename,
                                "contentType": a.contentType,
                            }
                            for a in dto.attachments
                        ]
                    ),
                    "mentions_json": "[]",
                    "embeds_json": "[]",
                    "reference_json": "null",
                    "components_json": "[]",
                    "reactions_json": "[]",
                }
                return dto, fragments

            monkeypatch.setattr(mc, "serialize_message", fake_serialize_message)

            class DummyWebhook:
                url = "http://example.com"

                async def send(self, *args, **kwargs):
                    files = kwargs.get("files") or []
                    attachments = [
                        types.SimpleNamespace(
                            url=f"http://example.com/{f.filename}",
                            filename=f.filename,
                            content_type="text/plain",
                        )
                        for f in files
                    ]
                    return types.SimpleNamespace(id=1, attachments=attachments)

            class DummyChannel:
                async def create_webhook(self, name: str):
                    return DummyWebhook()

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyChannel()

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord.abc, "Messageable", DummyChannel)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            from fastapi import UploadFile
            import io

            upload = UploadFile(filename="a.txt", file=io.BytesIO(b"hi"))
            body = mc.PostBody(channelId="123", content="hello")
            res = await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT, files=[upload])
            assert res["ok"] is True

            msg = (await db.execute(select(Message))).scalar_one()
            assert "a.txt" in (msg.attachments_json or "")

    asyncio.run(_run())


def test_discord_failure_details(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=3, discord_guild_id=3, name="Guild"))
            db.add(User(id=3, discord_user_id=30, global_name="Alice"))
            db.add(GuildChannel(guild_id=3, channel_id=123, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 3)
            user = await db.get(User, 3)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            class DummyResponse:
                status = 403
                reason = "Forbidden"

            class FailingWebhook:
                url = "http://example.com"

                async def send(self, *args, **kwargs):
                    raise mc.discord.HTTPException(DummyResponse(), {"message": "Missing Access", "code": 50001})

            class DummyChannel:
                async def create_webhook(self, name: str):
                    return FailingWebhook()

                async def send(self, *args, **kwargs):
                    raise mc.discord.HTTPException(DummyResponse(), {"message": "Forbidden", "code": 50013})

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyChannel()

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord.abc, "Messageable", DummyChannel)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="123", content="oops")
            with pytest.raises(HTTPException) as ex:
                await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert ex.value.status_code == 502
            detail = ex.value.detail
            assert isinstance(detail, dict)
            assert detail.get("message") == "Failed to relay message to Discord"
            disc = detail.get("discord")
            assert isinstance(disc, list) and any("Direct send failed" in d for d in disc)

    asyncio.run(_run())


def test_save_message_invalid_channel_id():
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=9, discord_guild_id=9, name="Guild"))
            db.add(User(id=9, discord_user_id=90, global_name="Alice"))
            await db.commit()
            guild = await db.get(Guild, 9)
            user = await db.get(User, 9)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            body = mc.PostBody(channelId="abc", content="hi")
            with pytest.raises(HTTPException) as ex:
                await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert ex.value.status_code == 400
            assert ex.value.detail == "invalid channel id"

    asyncio.run(_run())


def test_save_message_unresolved_channel_id(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=10, discord_guild_id=10, name="Guild"))
            db.add(User(id=10, discord_user_id=100, global_name="Alice"))
            db.add(GuildChannel(guild_id=10, channel_id=456, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 10)
            user = await db.get(User, 10)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            class DummyClient:
                def get_channel(self, cid: int):
                    return None

                async def fetch_channel(self, cid: int):
                    return None

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="456", content="hi")
            with pytest.raises(HTTPException) as ex:
                await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert ex.value.status_code == 404
            detail = ex.value.detail
            assert isinstance(detail, dict)
            assert detail.get("message") == "channel not found"

    asyncio.run(_run())


def test_save_message_unresolved_officer_channel(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=12, discord_guild_id=12, name="Guild"))
            db.add(User(id=12, discord_user_id=120, global_name="Alice"))
            db.add(
                GuildChannel(
                    guild_id=12,
                    channel_id=789,
                    kind=ChannelKind.OFFICER_CHAT,
                )
            )
            await db.commit()
            guild = await db.get(Guild, 12)
            user = await db.get(User, 12)
            ctx = RequestContext(
                user=user, guild=guild, key=DummyKey(), roles=["officer"]
            )

            class DummyClient:
                def get_channel(self, cid: int):
                    return None

                async def fetch_channel(self, cid: int):
                    return None

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="789", content="hi")
            with pytest.raises(HTTPException) as ex:
                await mc.save_message(
                    body,
                    ctx,
                    db,
                    channel_kind=ChannelKind.OFFICER_CHAT,
                )
            assert ex.value.status_code == 409
            detail = ex.value.detail
            assert isinstance(detail, dict)
            assert detail.get("code") == "OFFICER_CHANNEL_UNRESOLVED"
            assert detail.get("channelId") == "789"

    asyncio.run(_run())


def test_channel_not_found_returns_error_details(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=8, discord_guild_id=8, name="Guild"))
            db.add(User(id=8, discord_user_id=80, global_name="Alice"))
            db.add(GuildChannel(guild_id=8, channel_id=123, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 8)
            user = await db.get(User, 8)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            async def failing_webhook(*args, **kwargs):
                return None, None, ["Webhook boom"]

            monkeypatch.setattr(mc, "_send_via_webhook", failing_webhook)
            monkeypatch.setattr(mc, "discord_client", None)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="123", content="hi")
            with pytest.raises(HTTPException) as ex:
                await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert ex.value.status_code == 404
            detail = ex.value.detail
            assert isinstance(detail, dict)
            assert detail.get("message") == "channel not found"
            assert detail.get("discord") == ["Webhook boom"]

    asyncio.run(_run())


def test_attachment_validation(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=7, discord_guild_id=7, name="Guild"))
            db.add(User(id=7, discord_user_id=70, global_name="Alice"))
            db.add(GuildChannel(guild_id=7, channel_id=123, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 7)
            user = await db.get(User, 7)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            async def dummy_broadcast(message: str, guild_id: int, officer_only: bool = False, path: str | None = None):
                pass

            monkeypatch.setattr(mc.manager, "broadcast_text", dummy_broadcast)
            monkeypatch.setattr(mc.AttachmentDto, "contentType", property(lambda self: self.content_type), raising=False)

            class DummyWebhook:
                url = "http://example.com"

                async def send(self, *args, **kwargs):
                    return types.SimpleNamespace(id=1, attachments=[])

            class DummyChannel:
                async def create_webhook(self, name: str):
                    return DummyWebhook()

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyChannel()

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord.abc, "Messageable", DummyChannel)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            from fastapi import UploadFile
            import io

            body = mc.PostBody(channelId="123", content="hello")

            uploads = [UploadFile(filename=f"{i}.txt", file=io.BytesIO(b"hi")) for i in range(11)]
            with pytest.raises(HTTPException):
                await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT, files=uploads)

            big = UploadFile(filename="big.txt", file=io.BytesIO(b"x" * (25 * 1024 * 1024 + 1)))
            with pytest.raises(HTTPException):
                await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT, files=[big])

    asyncio.run(_run())


def test_officer_flow(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=2, discord_guild_id=2, name="Guild"))
            db.add(User(id=2, discord_user_id=20, global_name="Alice"))
            db.add(GuildChannel(guild_id=2, channel_id=123, kind=ChannelKind.OFFICER_CHAT))
            await db.commit()
            guild = await db.get(Guild, 2)
            user = await db.get(User, 2)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=["officer"])

            calls: List[Tuple[str, int, bool, str | None]] = []

            async def dummy_broadcast(message: str, guild_id: int, officer_only: bool = False, path: str | None = None):
                calls.append((message, guild_id, officer_only, path))

            monkeypatch.setattr(mc.manager, "broadcast_text", dummy_broadcast)

            class DummyWebhook:
                url = "http://example.com"

                async def send(self, *args, **kwargs):
                    return types.SimpleNamespace(id=2, attachments=[])

            class DummyChannel:
                async def create_webhook(self, name: str):
                    return DummyWebhook()

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyChannel()

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord.abc, "Messageable", DummyChannel)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="123", content="secret")
            res = await mc.save_message(body, ctx, db, channel_kind=ChannelKind.OFFICER_CHAT)
            assert res["ok"] is True

            msg = (await db.execute(select(Message))).scalar_one()
            assert msg.is_officer is True

            assert calls and calls[0][2] is True and calls[0][3] == "/ws/officer-messages"

            data = await mc.fetch_messages("123", ctx, db, is_officer=True)
            assert len(data) == 1 and data[0]["content"] == "secret"

    asyncio.run(_run())


def test_officer_requires_role(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=3, discord_guild_id=3, name="Guild"))
            db.add(User(id=3, discord_user_id=30, global_name="Alice"))
            db.add(GuildChannel(guild_id=3, channel_id=123, kind=ChannelKind.OFFICER_CHAT))
            await db.commit()
            guild = await db.get(Guild, 3)
            user = await db.get(User, 3)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            body = mc.PostBody(channelId="123", content="secret")
            with pytest.raises(HTTPException):
                await mc.save_message(body, ctx, db, channel_kind=ChannelKind.OFFICER_CHAT)
            with pytest.raises(HTTPException):
                await mc.fetch_messages("123", ctx, db, is_officer=True)

    asyncio.run(_run())


def test_save_message_webhook_failure(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=4, discord_guild_id=4, name="Guild"))
            db.add(User(id=4, discord_user_id=40, global_name="Alice"))
            db.add(GuildChannel(guild_id=4, channel_id=123, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 4)
            user = await db.get(User, 4)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            class DummyWebhook:
                url = "http://example.com"

                async def send(self, *args, **kwargs):
                    raise RuntimeError("fail")

            class DummyChannel:
                async def create_webhook(self, name: str):
                    return DummyWebhook()

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyChannel()

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord.abc, "Messageable", DummyChannel)
            monkeypatch.setattr(mc, "_channel_webhooks", {})

            body = mc.PostBody(channelId="123", content="oops")
            with pytest.raises(HTTPException) as exc:
                await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert exc.value.status_code == 502
            result = await db.execute(select(Message))
            assert result.first() is None

    asyncio.run(_run())


def test_webhook_errors_returned(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=5, discord_guild_id=5, name="Guild"))
            db.add(User(id=5, discord_user_id=50, global_name="Alice"))
            db.add(GuildChannel(guild_id=5, channel_id=123, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 5)
            user = await db.get(User, 5)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            async def dummy_broadcast(message: str, guild_id: int, officer_only: bool = False, path: str | None = None):
                return None

            async def dummy_webhook(*args, **kwargs):
                return 123, [], ["Webhook init failed"]

            monkeypatch.setattr(mc.manager, "broadcast_text", dummy_broadcast)
            monkeypatch.setattr(mc, "_send_via_webhook", dummy_webhook)
            monkeypatch.setattr(mc, "discord_client", None)

            body = mc.PostBody(channelId="123", content="hello")
            res = await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert res["ok"] is True
            assert res.get("detail", {}).get("discord") == ["Webhook init failed"]

    asyncio.run(_run())


def test_webhook_cache_persist_and_load(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=6, discord_guild_id=6, name="Guild"))
            db.add(User(id=6, discord_user_id=60, global_name="Alice"))
            db.add(
                Membership(
                    guild_id=6,
                    user_id=6,
                    nickname="AliceNick",
                    avatar_url="http://example.com/avatar.png",
                )
            )
            db.add(GuildChannel(guild_id=6, channel_id=321, kind=ChannelKind.FC_CHAT))
            await db.commit()
            guild = await db.get(Guild, 6)
            user = await db.get(User, 6)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            create_calls = 0
            send_calls = 0

            async def dummy_broadcast(message: str, guild_id: int, officer_only: bool = False, path: str | None = None):
                pass

            class DummyWebhook:
                url = "http://example.com"

                async def send(self, *args, **kwargs):
                    nonlocal send_calls
                    send_calls += 1
                    return types.SimpleNamespace(id=send_calls, attachments=[])

            class DummyChannel:
                async def create_webhook(self, name: str):
                    nonlocal create_calls
                    create_calls += 1
                    return DummyWebhook()

            class DummyClient:
                def get_channel(self, cid: int):
                    return DummyChannel()

            monkeypatch.setattr(mc, "discord_client", DummyClient())
            monkeypatch.setattr(mc.discord.abc, "Messageable", DummyChannel)
            monkeypatch.setattr(mc, "_channel_webhooks", {})
            monkeypatch.setattr(mc.manager, "broadcast_text", dummy_broadcast)

            def fake_serialize_message(message):
                dto = types.SimpleNamespace(
                    id=str(message.id),
                    channel_id=str(message.channel.id),
                    author_name="AliceNick",
                    author_avatar_url="http://example.com/avatar.png",
                    timestamp=datetime.utcnow(),
                    content=message.content,
                    attachments=[],
                    mentions=[],
                    author=types.SimpleNamespace(
                        name="AliceNick", avatar_url="http://example.com/avatar.png"
                    ),
                    embeds=[],
                    reference=None,
                    components=[],
                    reactions=[],
                    edited_timestamp=None,
                    model_dump=lambda **kwargs: {},
                    model_dump_json=lambda **kwargs: "{}",
                )
                fragments = {
                    "author_json": "{}",
                    "attachments_json": "[]",
                    "mentions_json": "[]",
                    "embeds_json": "[]",
                    "reference_json": "null",
                    "components_json": "[]",
                    "reactions_json": "[]",
                }
                return dto, fragments

            monkeypatch.setattr(mc, "serialize_message", fake_serialize_message)

            body = mc.PostBody(channelId="321", content="hello")
            res = await mc.save_message(body, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert res["ok"] is True

            stored = await db.scalar(
                select(GuildChannel.webhook_url).where(
                    GuildChannel.guild_id == guild.id,
                    GuildChannel.channel_id == 321,
                )
            )
            assert stored == "http://example.com"

            mc._channel_webhooks.clear()
            await mc.load_webhook_cache(db)
            assert mc._channel_webhooks[321] == "http://example.com"

            monkeypatch.setattr(
                mc.discord.Webhook,
                "from_url",
                lambda url, client=None: DummyWebhook(),
            )

            body2 = mc.PostBody(channelId="321", content="again")
            res2 = await mc.save_message(body2, ctx, db, channel_kind=ChannelKind.FC_CHAT)
            assert res2["ok"] is True
            assert create_calls == 1

    asyncio.run(_run())


def test_fetch_messages_pagination():
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            await db.execute(text("DELETE FROM guild_channels"))
            db.add(Guild(id=5, discord_guild_id=5, name="Guild"))
            db.add(User(id=5, discord_user_id=50, global_name="Alice"))
            await db.commit()
            guild = await db.get(Guild, 5)
            user = await db.get(User, 5)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])
            for i in range(1, 11):
                db.add(
                    Message(
                        discord_message_id=i,
                        channel_id=123,
                        guild_id=guild.id,
                        author_id=user.id,
                        author_name="Alice",
                        content_raw=str(i),
                        content_display=str(i),
                    )
                )
            await db.commit()

            msgs = await mc.fetch_messages("123", ctx, db, is_officer=False, limit=5)
            assert [int(m["id"]) for m in msgs] == [6, 7, 8, 9, 10]

            msgs = await mc.fetch_messages(
                "123", ctx, db, is_officer=False, limit=3, before="8"
            )
            assert [int(m["id"]) for m in msgs] == [5, 6, 7]

            msgs = await mc.fetch_messages("123", ctx, db, is_officer=False, after="8")
            assert [int(m["id"]) for m in msgs] == [9, 10]

            msgs = await mc.fetch_messages(
                "123", ctx, db, is_officer=False, before="10", after="3"
            )
            assert [int(m["id"]) for m in msgs] == [4, 5, 6, 7, 8, 9]

            msgs = await mc.fetch_messages(
                "123", ctx, db, is_officer=False, after="10"
            )
            assert msgs == []

            data = await mc.fetch_messages("123", ctx, db, is_officer=False, limit=2)
            assert [int(m["id"]) for m in data] == [9, 10]

    asyncio.run(_run())
