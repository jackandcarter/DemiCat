import types
import asyncio
import pytest
from fastapi import HTTPException, WebSocketDisconnect

from demibot.http import ws as ws_module
from demibot.http.deps import RequestContext

class StubWebSocket:
    def __init__(self):
        self.scope = {"path": "/ws/messages"}
        self.headers = {"X-Api-Key": "token"}
        self.query_params = {}
        self.close_code = None
        self.close_reason = None

    async def accept(self):
        pass

    async def close(self, code: int, reason: str):
        self.close_code = code
        self.close_reason = reason

    async def receive_text(self):
        raise WebSocketDisconnect()

def test_websocket_auth_success(monkeypatch):
    ws = StubWebSocket()
    guild = types.SimpleNamespace(id=1, discord_guild_id=1)
    user = types.SimpleNamespace(id=1, discord_user_id=1)
    ctx = RequestContext(user=user, guild=guild, key=None, roles=[])

    async def fake_get_session():
        yield None

    async def fake_api_key_auth(x_api_key, x_discord_id, db):
        assert x_discord_id is None
        return ctx

    connected = False

    async def fake_connect(*args, **kwargs):
        nonlocal connected
        connected = True

    monkeypatch.setattr(ws_module, "get_session", fake_get_session)
    monkeypatch.setattr(ws_module, "api_key_auth", fake_api_key_auth)
    monkeypatch.setattr(ws_module.manager, "connect", fake_connect)
    monkeypatch.setattr(ws_module.manager, "disconnect", lambda *a, **k: None)

    asyncio.run(ws_module.websocket_endpoint(ws))
    assert connected is True
    assert ws.close_code is None


def test_websocket_auth_failure(monkeypatch):
    ws = StubWebSocket()

    async def fake_get_session():
        yield None

    async def fake_api_key_auth(x_api_key, x_discord_id, db):
        assert x_discord_id is None
        raise HTTPException(status_code=401, detail="bad token")

    monkeypatch.setattr(ws_module, "get_session", fake_get_session)
    monkeypatch.setattr(ws_module, "api_key_auth", fake_api_key_auth)

    asyncio.run(ws_module.websocket_endpoint(ws))
    assert ws.close_code == 1008
    assert ws.close_reason == "auth failed"
