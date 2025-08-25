import asyncio
from typing import List, Tuple

import pytest
from fastapi import HTTPException
from sqlalchemy import select

from demibot.db.models import Guild, User, Message
from demibot.db.session import init_db, get_session
from demibot.http.deps import RequestContext
from demibot.http.routes import _messages_common as mc


class DummyKey:
    pass


def test_save_and_fetch_messages(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async for db in get_session():
            db.add(Guild(id=1, discord_guild_id=1, name="Guild"))
            db.add(User(id=1, discord_user_id=10, global_name="Alice"))
            await db.commit()
            guild = await db.get(Guild, 1)
            user = await db.get(User, 1)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            calls: List[Tuple[str, int, bool, str | None]] = []

            async def dummy_broadcast(message: str, guild_id: int, officer_only: bool = False, path: str | None = None):
                calls.append((message, guild_id, officer_only, path))

            monkeypatch.setattr(mc.manager, "broadcast_text", dummy_broadcast)

            body = mc.PostBody(channelId="123", content="hello")
            res = await mc.save_message(body, ctx, db, is_officer=False)
            assert res["ok"] is True

            msg = (await db.execute(select(Message))).scalar_one()
            assert msg.is_officer is False

            assert calls and calls[0][2] is False and calls[0][3] == "/ws/messages"

            data = await mc.fetch_messages("123", ctx, db, is_officer=False)
            assert len(data) == 1 and data[0]["content"] == "hello"
            break

    asyncio.run(_run())


def test_officer_flow(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async for db in get_session():
            db.add(Guild(id=1, discord_guild_id=1, name="Guild"))
            db.add(User(id=1, discord_user_id=10, global_name="Alice"))
            await db.commit()
            guild = await db.get(Guild, 1)
            user = await db.get(User, 1)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=["officer"])

            calls: List[Tuple[str, int, bool, str | None]] = []

            async def dummy_broadcast(message: str, guild_id: int, officer_only: bool = False, path: str | None = None):
                calls.append((message, guild_id, officer_only, path))

            monkeypatch.setattr(mc.manager, "broadcast_text", dummy_broadcast)

            body = mc.PostBody(channelId="123", content="secret")
            res = await mc.save_message(body, ctx, db, is_officer=True)
            assert res["ok"] is True

            msg = (await db.execute(select(Message))).scalar_one()
            assert msg.is_officer is True

            assert calls and calls[0][2] is True and calls[0][3] == "/ws/officer-messages"

            data = await mc.fetch_messages("123", ctx, db, is_officer=True)
            assert len(data) == 1 and data[0]["content"] == "secret"
            break

    asyncio.run(_run())


def test_officer_requires_role(monkeypatch):
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async for db in get_session():
            db.add(Guild(id=1, discord_guild_id=1, name="Guild"))
            db.add(User(id=1, discord_user_id=10, global_name="Alice"))
            await db.commit()
            guild = await db.get(Guild, 1)
            user = await db.get(User, 1)
            ctx = RequestContext(user=user, guild=guild, key=DummyKey(), roles=[])

            body = mc.PostBody(channelId="123", content="secret")
            with pytest.raises(HTTPException):
                await mc.save_message(body, ctx, db, is_officer=True)
            with pytest.raises(HTTPException):
                await mc.fetch_messages("123", ctx, db, is_officer=True)
            break

    asyncio.run(_run())
