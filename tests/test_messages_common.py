import asyncio
import types
from typing import List, Tuple
from datetime import datetime
import json

import pytest
from fastapi import HTTPException
from sqlalchemy import select, text

from demibot.db.models import Guild, User, Message, Membership, GuildChannel
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
            res = await mc.save_message(body, ctx, db, is_officer=False)
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


def test_multipart_message(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            db.add(Guild(id=9, discord_guild_id=9, name="Guild"))
            db.add(User(id=9, discord_user_id=90, global_name="Alice"))
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
                    model_dump=lambda: {},
                    model_dump_json=lambda: "{}",
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
            res = await mc.save_message(body, ctx, db, is_officer=False, files=[upload])
            assert res["ok"] is True

            msg = (await db.execute(select(Message))).scalar_one()
            assert "a.txt" in (msg.attachments_json or "")

    asyncio.run(_run())


def test_officer_flow(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
            db.add(Guild(id=2, discord_guild_id=2, name="Guild"))
            db.add(User(id=2, discord_user_id=20, global_name="Alice"))
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
            res = await mc.save_message(body, ctx, db, is_officer=True)
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
            db.add(Guild(id=3, discord_guild_id=3, name="Guild"))
            db.add(User(id=3, discord_user_id=30, global_name="Alice"))
            await db.commit()
            guild = await db.get(Guild, 3)
            user = await db.get(User, 3)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            body = mc.PostBody(channelId="123", content="secret")
            with pytest.raises(HTTPException):
                await mc.save_message(body, ctx, db, is_officer=True)
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
            db.add(Guild(id=4, discord_guild_id=4, name="Guild"))
            db.add(User(id=4, discord_user_id=40, global_name="Alice"))
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
                await mc.save_message(body, ctx, db, is_officer=False)
            assert exc.value.status_code == 502
            result = await db.execute(select(Message))
            assert result.first() is None

    asyncio.run(_run())


def test_webhook_cache_persist_and_load(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.execute(text("DELETE FROM messages"))
            await db.execute(text("DELETE FROM memberships"))
            await db.execute(text("DELETE FROM users"))
            await db.execute(text("DELETE FROM guilds"))
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
                    model_dump=lambda: {},
                    model_dump_json=lambda: "{}",
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
            res = await mc.save_message(body, ctx, db, is_officer=False)
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
            res2 = await mc.save_message(body2, ctx, db, is_officer=False)
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
