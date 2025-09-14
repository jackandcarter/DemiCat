import asyncio
import importlib.util
import sys
import types
from pathlib import Path

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

# Create package stubs so relative imports work
pkg = types.ModuleType("demibot")
pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", pkg)
http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)
routes_pkg = types.ModuleType("demibot.http.routes")
routes_pkg.__path__ = [str(root / "demibot/http/routes")]
sys.modules.setdefault("demibot.http.routes", routes_pkg)

spec = importlib.util.spec_from_file_location(
    "demibot.http.routes.guild_roles", root / "demibot/http/routes/guild_roles.py"
)
guild_roles = importlib.util.module_from_spec(spec)
sys.modules["demibot.http.routes.guild_roles"] = guild_roles
spec.loader.exec_module(guild_roles)

from demibot.http.deps import RequestContext
from demibot.db.models import Guild, GuildConfig, Role
from demibot.db.session import init_db, get_session

class StubContext(RequestContext):
    def __init__(self, guild_id: int, roles: list[str]):
        self.guild = types.SimpleNamespace(id=guild_id, discord_guild_id=guild_id)
        self.roles = roles
        self.key = None
        self.user = None

def test_guild_roles_filter():
    async def _run():
        db_path = Path("test_guild_roles.db")
        if db_path.exists():
            db_path.unlink()
        await init_db(f"sqlite+aiosqlite:///{db_path}")
        async with get_session() as db:
            guild = Guild(id=1, discord_guild_id=1, name="Test")
            db.add(guild)
            db.add(GuildConfig(guild_id=1, mention_role_ids="1,2"))
            db.add_all(
                [
                    Role(guild_id=1, discord_role_id=1, name="One"),
                    Role(guild_id=1, discord_role_id=2, name="Two"),
                    Role(guild_id=1, discord_role_id=3, name="Three"),
                ]
            )
            await db.commit()
        ctx = StubContext(1, [])
        async with get_session() as db:
            roles = await guild_roles.get_guild_roles(ctx=ctx, db=db)
            assert {r["id"] for r in roles} == {"1", "2"}
        ctx_off = StubContext(1, ["officer"])
        async with get_session() as db:
            roles = await guild_roles.get_guild_roles(ctx=ctx_off, db=db)
            assert {r["id"] for r in roles} == {"1", "2", "3"}
    asyncio.run(_run())
