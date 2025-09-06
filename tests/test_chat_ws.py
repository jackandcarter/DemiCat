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

        async def fake_auth(x_api_key, x_discord_id, db):
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

        async def fake_auth(x_api_key, x_discord_id, db):
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

