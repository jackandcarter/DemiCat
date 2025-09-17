import asyncio
import json
import types
import pytest
from contextlib import asynccontextmanager
from fastapi import HTTPException, WebSocketDisconnect
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "demibot"))

from demibot.http import ws_chat
from demibot.http.deps import RequestContext


class StubWebSocket:
    def __init__(self, token: str | None = "token"):
        self.scope = {"path": "/ws/chat"}
        self.headers = {"X-Api-Key": token} if token is not None else {}
        self.query_params = {}
        self.close_code: int | None = None
        self.close_reason: str | None = None
        self.sent: list[str] = []
        self.messages: list[dict] = []

    async def accept(self, headers=None):  # pragma: no cover - noop
        pass

    async def send_text(self, message: str):
        self.sent.append(message)

    async def close(self, code: int, reason: str):
        self.close_code = code
        self.close_reason = reason

    async def receive_json(self):
        if self.messages:
            return self.messages.pop(0)
        raise WebSocketDisconnect()

    async def ping(self):
        return None


class DummySession:
    async def close(self):  # pragma: no cover - trivial
        pass


def _run(coro):
    asyncio.run(coro)


def test_chat_ws_role_access(monkeypatch):
    async def scenario(roles, expect_resync):
        ws = StubWebSocket()
        ws.messages = [{"op": "sub", "channels": [{"id": "1", "officer": True}]}]
        guild = types.SimpleNamespace(id=1, discord_guild_id=1)
        user = types.SimpleNamespace(id=1, discord_user_id=1, character_name="a")
        ctx = RequestContext(user=user, guild=guild, key=None, roles=roles)

        @asynccontextmanager
        async def fake_get_session():
            yield DummySession()

        async def fake_auth(request, x_api_key, x_discord_id, db):
            assert request.method == "WS"
            return ctx

        manager = ws_chat.ChatConnectionManager()
        monkeypatch.setattr(ws_chat, "get_session", fake_get_session)
        monkeypatch.setattr(ws_chat, "api_key_auth", fake_auth)
        monkeypatch.setattr(ws_chat, "manager", manager)

        await ws_chat.websocket_endpoint_chat(ws)
        if expect_resync:
            assert len(ws.sent) == 1
            assert json.loads(ws.sent[0])["op"] == "resync"
        else:
            assert ws.sent == []

    _run(scenario(["officer"], True))
    _run(scenario([], False))


def test_chat_ws_invalid_token(monkeypatch):
    async def scenario():
        ws = StubWebSocket(token="bad")

        @asynccontextmanager
        async def fake_get_session():
            yield DummySession()

        async def fake_auth(request, x_api_key, x_discord_id, db):
            assert request.method == "WS"
            raise HTTPException(status_code=401, detail="bad")

        monkeypatch.setattr(ws_chat, "get_session", fake_get_session)
        monkeypatch.setattr(ws_chat, "api_key_auth", fake_auth)

        await ws_chat.websocket_endpoint_chat(ws)
        assert ws.close_code == 1008
        assert ws.close_reason == "auth failed"

    _run(scenario())


def test_chat_ws_missing_token():
    async def scenario():
        ws = StubWebSocket(token=None)
        await ws_chat.websocket_endpoint_chat(ws)
        assert ws.close_code == 1008
        assert ws.close_reason == "missing token"

    _run(scenario())


def test_chat_ws_frames_include_metadata(monkeypatch):
    async def scenario():
        manager = ws_chat.ChatConnectionManager()
        ws = StubWebSocket()
        meta = ws_chat.ChannelMeta(guild_id=1, discord_guild_id=321, kind="CHAT")

        await manager._send_subscription_ack(ws, "42", meta)
        ack = json.loads(ws.sent[-1])
        assert ack["guildId"] == "321"
        assert ack["kind"] == "CHAT"

        await manager._send_resync(ws, "42", meta)
        resync = json.loads(ws.sent[-1])
        assert resync["guildId"] == "321"
        assert resync["kind"] == "CHAT"

        ws.sent.clear()

        ctx = RequestContext(
            user=types.SimpleNamespace(character_name=None),
            guild=types.SimpleNamespace(id=1, discord_guild_id=321),
            key=types.SimpleNamespace(),
            roles=[],
        )
        info = ws_chat.ChatConnection(ctx=ctx)
        info.channels.add("42")
        info.metadata["42"] = meta
        manager.connections[ws] = info
        manager._channel_meta["42"] = meta
        manager._channel_queues["42"] = [{"cursor": 7, "op": "mc", "d": {}}]

        async def fake_sleep(delay):
            return None

        monkeypatch.setattr(ws_chat.asyncio, "sleep", fake_sleep)
        monkeypatch.setattr(ws_chat.random, "uniform", lambda a, b: 0.0)

        await manager._flush_channel("42")
        batch = json.loads(ws.sent[-1])
        assert batch["guildId"] == "321"
        assert batch["kind"] == "CHAT"

    _run(scenario())


def test_chat_ws_mixed_channel_batch_rejected(monkeypatch):
    async def scenario():
        manager = ws_chat.ChatConnectionManager()
        ws = StubWebSocket()
        ctx = RequestContext(
            user=types.SimpleNamespace(character_name=None),
            guild=types.SimpleNamespace(id=1, discord_guild_id=1),
            key=types.SimpleNamespace(),
            roles=[],
        )
        info = ws_chat.ChatConnection(ctx=ctx)
        info.channels.add("42")
        info.metadata["42"] = ws_chat.ChannelMeta(guild_id=1, discord_guild_id=None, kind="FC_CHAT")
        manager.connections[ws] = info
        manager._channel_meta["42"] = ws_chat.ChannelMeta(guild_id=1, discord_guild_id=None, kind="CHAT")
        manager._channel_queues["42"] = [{"cursor": 1, "op": "mc", "d": {}}]

        async def fake_sleep(delay):
            return None

        monkeypatch.setattr(ws_chat.asyncio, "sleep", fake_sleep)
        monkeypatch.setattr(ws_chat.random, "uniform", lambda a, b: 0.0)

        await manager._flush_channel("42")
        assert ws.sent == []

    _run(scenario())

