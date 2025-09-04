import sys
from pathlib import Path
import types
import asyncio
from types import SimpleNamespace

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.db.models import Guild, Role
from demibot.db.session import init_db, get_session
from demibot.http.routes.guild_roles import get_guild_roles


async def _run_test() -> None:
    db_path = Path("test_roles.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        db.add(guild)
        db.add(Role(guild_id=guild.id, discord_role_id=10, name="Alpha"))
        db.add(Role(guild_id=guild.id, discord_role_id=20, name="Beta"))
        await db.commit()
        ctx = SimpleNamespace(guild=guild)
        res = await get_guild_roles(ctx=ctx, db=db)
        pairs = {(r["id"], r["name"]) for r in res}
        assert pairs == {("10", "Alpha"), ("20", "Beta")}


def test_get_guild_roles() -> None:
    asyncio.run(_run_test())

