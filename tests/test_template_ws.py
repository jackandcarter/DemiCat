import types
import asyncio
import json
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

structlog_stub = types.ModuleType("structlog")
structlog_stub.get_logger = lambda *a, **k: None
sys.modules.setdefault("structlog", structlog_stub)

db_pkg = types.ModuleType("demibot.db")
db_pkg.__path__ = [str(root / "demibot/db")]
session_pkg = types.ModuleType("demibot.db.session")
async def get_session():
    pass
session_pkg.get_session = get_session
sys.modules.setdefault("demibot.db", db_pkg)
sys.modules.setdefault("demibot.db.session", session_pkg)

deps_pkg = types.ModuleType("demibot.http.deps")
class RequestContext:
    def __init__(self, guild, roles):
        self.guild = guild
        self.roles = roles
async def api_key_auth(*args, **kwargs):
    pass
deps_pkg.RequestContext = RequestContext
deps_pkg.api_key_auth = api_key_auth
sys.modules.setdefault("demibot.http.deps", deps_pkg)

fastapi_stub = types.ModuleType("fastapi")
class _WebSocket:  # minimal stub
    pass
fastapi_stub.WebSocket = _WebSocket
fastapi_stub.WebSocketDisconnect = type("WebSocketDisconnect", (Exception,), {})
fastapi_stub.HTTPException = type("HTTPException", (Exception,), {})
sys.modules.setdefault("fastapi", fastapi_stub)

from demibot.http.ws import ConnectionManager


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


def test_templates_updates_broadcast_to_all():
    async def _run():
        manager = ConnectionManager()
        ws_officer = StubWebSocket("/ws/templates")
        ctx_officer = StubContext(1, ["officer"])
        await manager.connect(ws_officer, ctx_officer)
        ws_member = StubWebSocket("/ws/templates")
        ctx_member = StubContext(1, [])
        await manager.connect(ws_member, ctx_member)

        msg = json.dumps({"topic": "templates.updated"})
        await manager.broadcast_text(msg, 1, path="/ws/templates")

        assert ws_officer.sent == [msg]
        assert ws_member.sent == [msg]

    asyncio.run(_run())
