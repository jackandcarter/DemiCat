import types
import asyncio
import json
import sys
from pathlib import Path

# Setup module paths
root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

# Package stubs
demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)
http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

# Stubs for RequestContext
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

# Minimal discord/fastapi stubs
sys.modules.setdefault("discord", types.ModuleType("discord"))
fastapi_stub = types.ModuleType("fastapi")
fastapi_stub.WebSocket = type("WebSocket", (), {})
fastapi_stub.WebSocketDisconnect = type("WebSocketDisconnect", (Exception,), {})
sys.modules.setdefault("fastapi", fastapi_stub)

from demibot.http.ws import ConnectionManager
from demibot.http.routes import templates
from demibot.db.models import Guild, GuildChannel, ChannelKind
from demibot.db.session import init_db, get_session
from demibot.http.schemas import TemplatePayload

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

def test_template_create_delete_sync(monkeypatch):
    async def _run():
        mgr = ConnectionManager()
        monkeypatch.setattr(templates, "manager", mgr)

        ws1 = StubWebSocket("/ws/templates")
        ws2 = StubWebSocket("/ws/templates")
        ctx1 = StubContext(1, [])
        ctx2 = StubContext(1, [])
        await mgr.connect(ws1, ctx1)
        await mgr.connect(ws2, ctx2)

        db_path = Path("test_templates_sync.db")
        if db_path.exists():
            db_path.unlink()
        await init_db(f"sqlite+aiosqlite:///{db_path}")
        async with get_session() as db:
            guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
            db.add(guild)
            db.add(GuildChannel(guild_id=guild.id, channel_id=123, kind=ChannelKind.EVENT))
            await db.commit()

        payload = TemplatePayload(channelId="123", title="Test", time="2024-01-01T00:00:00Z", description="desc")
        body = templates.TemplateCreateBody(name="Raid", description="templ", payload=payload)
        ctx = StubContext(1, [])
        async with get_session() as db:
            dto = await templates.create_template(body=body, ctx=ctx, db=db)
            msg1 = json.loads(ws1.sent[0])
            msg2 = json.loads(ws2.sent[0])
            assert msg1 == msg2
            assert msg1["topic"] == "templates.updated"
            assert msg1["payload"]["id"] == dto.id
            ws1.sent.clear(); ws2.sent.clear()
            await templates.delete_template(template_id=dto.id, ctx=ctx, db=db)
            msg1 = json.loads(ws1.sent[0])
            msg2 = json.loads(ws2.sent[0])
            assert msg1 == msg2
            assert msg1["payload"] == {"id": dto.id, "deleted": True}
    asyncio.run(_run())
