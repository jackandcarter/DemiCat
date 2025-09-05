import asyncio
import types
import pytest
from contextlib import asynccontextmanager

from demibot.http.ws import ConnectionManager
from demibot.http import ws as ws_module
from demibot.http.deps import RequestContext

class StubWebSocket:
    def __init__(self, path: str):
        self.scope = {"path": path}
        self.sent: list[str] = []
    async def accept(self):
        pass
    async def send_text(self, message: str):
        self.sent.append(message)

    async def ping(self):
        return None

class StubContext:
    def __init__(self, guild_id: int, roles):
        self.guild = types.SimpleNamespace(id=guild_id)
        self.roles = roles

def test_officer_broadcast_filtered_by_path_and_role():
    async def _run():
        manager = ConnectionManager()
        # Officer connected to officer path
        ws_officer = StubWebSocket("/ws/officer-messages")
        ctx_officer = StubContext(1, ["officer"])
        await manager.connect(ws_officer, ctx_officer)

        # Officer connected to general path
        ws_general = StubWebSocket("/ws/messages")
        ctx_general = StubContext(1, ["officer"])
        await manager.connect(ws_general, ctx_general)

        # Non-officer connected to officer path
        ws_non_officer = StubWebSocket("/ws/officer-messages")
        ctx_non_officer = StubContext(1, [])
        await manager.connect(ws_non_officer, ctx_non_officer)

        # Officer from another guild
        ws_other_guild = StubWebSocket("/ws/officer-messages")
        ctx_other_guild = StubContext(2, ["officer"])
        await manager.connect(ws_other_guild, ctx_other_guild)

        await manager.broadcast_text("hi", 1, officer_only=True)

        assert ws_officer.sent == ["hi"]
        assert ws_general.sent == []
        assert ws_non_officer.sent == []
        assert ws_other_guild.sent == []

    asyncio.run(_run())


class EndpointStubWebSocket:
    def __init__(self, path: str):
        self.scope = {"path": path}
        self.headers = {"X-Api-Key": "token"}
        self.query_params = {}
        self.close_code: int | None = None
        self.close_reason: str | None = None

    async def accept(self) -> None:  # pragma: no cover - noop
        pass

    async def close(self, code: int, reason: str) -> None:
        self.close_code = code
        self.close_reason = reason

    async def receive(self) -> str:  # pragma: no cover - should not be called
        raise RuntimeError("receive should not be called")

    async def ping(self):
        return None


def test_officer_path_requires_role(monkeypatch):
    async def _run():
        ws = EndpointStubWebSocket("/ws/officer-messages")

        guild = types.SimpleNamespace(id=1, discord_guild_id=1)
        user = types.SimpleNamespace(id=1, discord_user_id=1)
        ctx = RequestContext(user=user, guild=guild, key=None, roles=[])

        class DummySession:
            def __init__(self):
                self.closed = False

            async def close(self):  # pragma: no cover - trivial
                self.closed = True

        session = DummySession()

        @asynccontextmanager
        async def fake_get_session():
            try:
                yield session
            finally:
                await session.close()

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

        await ws_module.websocket_endpoint(ws)

        assert ws.close_code == 1008
        assert ws.close_reason == "unauthorized"
        assert connected is False
        assert session.closed is True

    asyncio.run(_run())
