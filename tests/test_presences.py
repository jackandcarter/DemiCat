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
from demibot.discordbot.presence_store import set_presence, Presence as StorePresence
from demibot.db.models import Presence as DbPresence, User
from demibot.db.session import init_db, get_session
from demibot.db import session as db_session
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
        db_session._engine = None
        db_session._Session = None
        set_presence(
            1,
            StorePresence(
                id=10,
                name="Alice",
                status="online",
                avatar_url="https://example.com/a.png",
                roles=[100],
            ),
        )
        set_presence(
            1,
            StorePresence(
                id=20,
                name="Bob",
                status="offline",
                avatar_url="https://example.com/b.png",
                roles=[],
            ),
        )
        ctx = StubContext(1)
        res = await list_presences(ctx=ctx)
        assert {
            (p["id"], p["status"], p["avatar_url"], tuple(p["roles"])) for p in res
        } == {
            ("10", "online", "https://example.com/a.png", ("100",)),
            ("20", "offline", "https://example.com/b.png", ()),
        }

    asyncio.run(_run())


def test_list_presences_reads_from_db():
    async def _run():
        db_session._engine = None
        db_session._Session = None
        url = "sqlite+aiosqlite://"
        await init_db(url)
        async with get_session() as db:
            db.add(User(id=1, discord_user_id=10, global_name="Alice"))
            db.add(User(id=2, discord_user_id=20, global_name="Bob"))
            db.add(DbPresence(guild_id=1, user_id=10, status="online"))
            db.add(DbPresence(guild_id=1, user_id=20, status="offline"))
            await db.commit()
        ctx = StubContext(1)
        res = await list_presences(ctx=ctx)
        assert {
            (p["id"], p["status"], p["avatar_url"], tuple(p["roles"])) for p in res
        } == {("10", "online", None, ()), ("20", "offline", None, ())}
    asyncio.run(_run())
