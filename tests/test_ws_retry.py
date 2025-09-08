import asyncio
import types
import asyncio
import types
from contextlib import asynccontextmanager

import os
import sys

import discord
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "demibot"))

import types as _types

_alembic = _types.ModuleType("alembic")
_alembic.command = _types.SimpleNamespace()
_alembic.config = _types.SimpleNamespace(Config=object)
sys.modules.setdefault("alembic", _alembic)
sys.modules.setdefault("alembic.command", _alembic.command)
sys.modules.setdefault("alembic.config", _alembic.config)

from demibot.http import ws_chat
from demibot.http.deps import RequestContext


def _make_ctx() -> RequestContext:
    guild = types.SimpleNamespace(id=1)
    user = types.SimpleNamespace(character_name="Alice")
    return RequestContext(user=user, guild=guild, key=None, roles=[])


class DummyResponse:
    def __init__(self, headers=None, status=429):
        self.headers = headers or {}
        self.status = status


def test_webhook_retry_success(monkeypatch):
    async def _run():
        ctx = _make_ctx()
        manager = ws_chat.ChatConnectionManager()
        ws = object()
        manager.connections[ws] = ws_chat.ChatConnection(ctx)

        @asynccontextmanager
        async def fake_get_session():
            class DummyDB:
                async def scalar(self, query):
                    return "http://example.com"

            yield DummyDB()

        monkeypatch.setattr(ws_chat, "get_session", fake_get_session)

        events = []

        async def fake_emit(evt):
            events.append(evt)

        monkeypatch.setattr(ws_chat, "emit_event", fake_emit)

        call_count = {"n": 0}

        class DummyWebhook:
            async def send(self, *args, **kwargs):
                call_count["n"] += 1
                if call_count["n"] == 1:
                    raise discord.HTTPException(
                        DummyResponse({"Retry-After": "0"}), "rate limit"
                    )
                return types.SimpleNamespace(
                    id=1,
                    channel=types.SimpleNamespace(id=123),
                    attachments=[],
                    content=args[0],
                )

        monkeypatch.setattr(
            ws_chat.discord.Webhook, "from_url", lambda url, client=None: DummyWebhook()
        )

        def fake_serialize_message(message):
            class Dummy:
                def model_dump(self, **kwargs):
                    return {"id": "1"}

            return Dummy(), None

        monkeypatch.setattr(ws_chat, "serialize_message", fake_serialize_message)
        monkeypatch.setattr(ws_chat, "RETRY_BASE", 0.0)
        monkeypatch.setattr(ws_chat, "MAX_SEND_ATTEMPTS", 2)

        data = {"ch": 123, "d": {"content": "hi"}}
        await manager._handle_send(ws, data)
        await asyncio.wait_for(manager._webhook_tasks[123], 1)

        assert call_count["n"] == 2
        assert events and events[0]["op"] == "mc"

    asyncio.run(_run())


def test_webhook_retry_failure(monkeypatch):
    async def _run():
        ctx = _make_ctx()
        manager = ws_chat.ChatConnectionManager()
        ws = object()
        manager.connections[ws] = ws_chat.ChatConnection(ctx)

        @asynccontextmanager
        async def fake_get_session():
            class DummyDB:
                async def scalar(self, query):
                    return "http://example.com"

            yield DummyDB()

        monkeypatch.setattr(ws_chat, "get_session", fake_get_session)

        events = []

        async def fake_emit(evt):
            events.append(evt)

        monkeypatch.setattr(ws_chat, "emit_event", fake_emit)

        call_count = {"n": 0}

        class DummyWebhookFail:
            async def send(self, *args, **kwargs):
                call_count["n"] += 1
                raise discord.HTTPException(
                    DummyResponse({"Retry-After": "0"}), "rate limit"
                )

        monkeypatch.setattr(
            ws_chat.discord.Webhook,
            "from_url",
            lambda url, client=None: DummyWebhookFail(),
        )

        def fake_serialize_message(message):
            class Dummy:
                def model_dump(self, **kwargs):
                    return {"id": "1"}

            return Dummy(), None

        monkeypatch.setattr(ws_chat, "serialize_message", fake_serialize_message)
        monkeypatch.setattr(ws_chat, "RETRY_BASE", 0.0)
        monkeypatch.setattr(ws_chat, "MAX_SEND_ATTEMPTS", 2)

        data = {"ch": 123, "d": {"content": "hi"}}
        await manager._handle_send(ws, data)
        await asyncio.wait_for(manager._webhook_tasks[123], 1)

        assert call_count["n"] == 2
        assert events and events[0]["op"] == "mf"

    asyncio.run(_run())
