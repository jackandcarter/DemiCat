import sys
import types
from pathlib import Path
import asyncio

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

routes_pkg = types.ModuleType("demibot.http.routes")
routes_pkg.__path__ = [str(root / "demibot/http/routes")]
sys.modules.setdefault("demibot.http.routes", routes_pkg)

# stub discord dependency
import types as _types

discord_mod = _types.ModuleType("discord")
abc_mod = _types.ModuleType("discord.abc")
abc_mod.Messageable = type("Messageable", (), {})
discord_mod.abc = abc_mod
sys.modules.setdefault("discord", discord_mod)
sys.modules.setdefault("discord.abc", abc_mod)
ext_mod = _types.ModuleType("discord.ext")
commands_mod = _types.ModuleType("discord.ext.commands")
ext_mod.commands = commands_mod
discord_mod.ext = ext_mod
sys.modules.setdefault("discord.ext", ext_mod)
sys.modules.setdefault("discord.ext.commands", commands_mod)

from types import SimpleNamespace

from demibot.db.models import Guild, User, Request as DbRequest, RequestStatus, RequestType, Urgency
from demibot.db.session import init_db, get_session
from demibot.http.deps import RequestContext
import demibot.http.routes.requests as request_routes

async def _setup_db(db_path: str) -> None:
    await init_db(f"sqlite+aiosqlite:///{db_path}")
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        user = User(id=1, discord_user_id=1)
        db.add_all([guild, user])
        await db.commit()

import pytest

@pytest.fixture()
def db_setup(tmp_path):
    path = tmp_path / "requests.db"
    asyncio.run(_setup_db(str(path)))
    return path


def test_requests_delta(db_setup):
    async def run():
        async with get_session() as db:
            guild = await db.get(Guild, 1)
            user = await db.get(User, 1)
            req = DbRequest(
                id=1,
                guild_id=guild.id,
                user_id=user.id,
                title="Test",
                type=RequestType.ITEM,
                status=RequestStatus.OPEN,
                urgency=Urgency.LOW,
            )
            db.add(req)
            await db.commit()
            await db.refresh(req)
            ctx = RequestContext(user=user, guild=guild, key=SimpleNamespace(), roles=[])
            since = req.updated_at
            # no changes yet
            res1 = await request_routes.list_request_deltas(since=since, ctx=ctx, db=db)
            # update request
            req.title = "Updated"
            await db.commit()
            res2 = await request_routes.list_request_deltas(since=since, ctx=ctx, db=db)
            return res1, res2
    first, second = asyncio.run(run())
    assert first == []
    assert len(second) == 1
    assert second[0]["title"] == "Updated"
