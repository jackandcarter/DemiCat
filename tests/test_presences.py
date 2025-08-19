import types
from pathlib import Path
import sys
import pytest

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))
demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)
http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.http.ws import ConnectionManager
from demibot.http.routes.presences import list_presences
from demibot.discordbot.presence_store import set_presence, Presence
import asyncio


class StubWebSocket:
    def __init__(self, path: str):
        self.scope = {"path": path}
        self.sent: list[str] = []

    async def accept(self):
        pass

    async def send_text(self, message: str):
        self.sent.append(message)


class StubContext:
    def __init__(self, guild_id: int):
        self.guild = types.SimpleNamespace(id=guild_id)
        self.roles = []


def test_presence_broadcast_filtered_by_path():
    async def _run():
        manager = ConnectionManager()
        ws_presence = StubWebSocket("/ws/presences")
        ctx = StubContext(1)
        await manager.connect(ws_presence, ctx)
        ws_other = StubWebSocket("/ws/messages")
        await manager.connect(ws_other, ctx)

        await manager.broadcast_text("hi", 1, path="/ws/presences")
        assert ws_presence.sent == ["hi"]
        assert ws_other.sent == []

    asyncio.run(_run())


def test_list_presences_returns_data():
    async def _run():
        set_presence(1, Presence(id=10, name="Alice", status="online"))
        set_presence(1, Presence(id=20, name="Bob", status="offline"))
        ctx = StubContext(1)
        res = await list_presences(ctx=ctx)
        assert {(p["id"], p["status"]) for p in res} == {("10", "online"), ("20", "offline")}

    asyncio.run(_run())
