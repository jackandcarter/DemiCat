import asyncio
from types import SimpleNamespace

import pytest
from fastapi import HTTPException

import asyncio
import sys
import types
from pathlib import Path
from types import SimpleNamespace

import pytest
from fastapi import HTTPException

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

routes_pkg = types.ModuleType("demibot.http.routes")
routes_pkg.__path__ = [str(root / "demibot/http/routes")]
sys.modules.setdefault("demibot.http.routes", routes_pkg)

discord_mod = types.ModuleType("discord")
abc_mod = types.ModuleType("discord.abc")
abc_mod.Messageable = type("Messageable", (), {})
discord_mod.abc = abc_mod
sys.modules.setdefault("discord", discord_mod)
sys.modules.setdefault("discord.abc", abc_mod)
ext_mod = types.ModuleType("discord.ext")
commands_mod = types.ModuleType("discord.ext.commands")
ext_mod.commands = commands_mod
discord_mod.ext = ext_mod
sys.modules.setdefault("discord.ext", ext_mod)
sys.modules.setdefault("discord.ext.commands", commands_mod)
from demibot.db.models import (
    Guild,
    User,
    Request as DbRequest,
    RequestStatus,
    RequestType,
    Urgency,
)
from demibot.db.session import init_db, get_session
from demibot.http.deps import RequestContext
import demibot.http.routes.requests as request_routes


async def _setup_db(db_path: str) -> None:
    await init_db(f"sqlite+aiosqlite:///{db_path}")
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        requester = User(id=1, discord_user_id=1)
        assignee = User(id=2, discord_user_id=2)
        other = User(id=3, discord_user_id=3)
        db.add_all([guild, requester, assignee, other])
        await db.commit()


@pytest.fixture()
def db_setup(tmp_path):
    path = tmp_path / "requests.db"
    asyncio.run(_setup_db(str(path)))
    return path


async def _noop(*args, **kwargs):
    return None


@pytest.fixture(autouse=True)
def patch_broadcast(monkeypatch):
    monkeypatch.setattr(request_routes, "_broadcast", _noop)
    monkeypatch.setattr(request_routes, "_notify", _noop)


def test_start_requires_assignee(db_setup):
    async def run():
        async with get_session() as db:
            guild = await db.get(Guild, 1)
            requester = await db.get(User, 1)
            assignee = await db.get(User, 2)
            other = await db.get(User, 3)
            req = DbRequest(
                id=1,
                guild_id=guild.id,
                user_id=requester.id,
                assignee_id=assignee.id,
                title="Test",
                type=RequestType.ITEM,
                status=RequestStatus.CLAIMED,
                urgency=Urgency.LOW,
            )
            db.add(req)
            await db.commit()
            ctx = RequestContext(user=other, guild=guild, key=SimpleNamespace(), roles=[])
            body = request_routes.StatusBody(version=req.version)
            with pytest.raises(HTTPException) as exc:
                await request_routes.start_request(req.id, body, ctx=ctx, db=db)
            return exc.value.status_code
    code = asyncio.run(run())
    assert code == 403


def test_complete_requires_assignee(db_setup):
    async def run():
        async with get_session() as db:
            guild = await db.get(Guild, 1)
            requester = await db.get(User, 1)
            assignee = await db.get(User, 2)
            other = await db.get(User, 3)
            req = DbRequest(
                id=2,
                guild_id=guild.id,
                user_id=requester.id,
                assignee_id=assignee.id,
                title="Test",
                type=RequestType.ITEM,
                status=RequestStatus.IN_PROGRESS,
                urgency=Urgency.LOW,
            )
            db.add(req)
            await db.commit()
            ctx = RequestContext(user=other, guild=guild, key=SimpleNamespace(), roles=[])
            body = request_routes.StatusBody(version=req.version)
            with pytest.raises(HTTPException) as exc:
                await request_routes.complete_request(req.id, body, ctx=ctx, db=db)
            return exc.value.status_code
    code = asyncio.run(run())
    assert code == 403


def test_confirm_requires_requester(db_setup):
    async def run():
        async with get_session() as db:
            guild = await db.get(Guild, 1)
            requester = await db.get(User, 1)
            assignee = await db.get(User, 2)
            other = await db.get(User, 3)
            req = DbRequest(
                id=3,
                guild_id=guild.id,
                user_id=requester.id,
                assignee_id=assignee.id,
                title="Test",
                type=RequestType.ITEM,
                status=RequestStatus.AWAITING_CONFIRM,
                urgency=Urgency.LOW,
            )
            db.add(req)
            await db.commit()
            ctx = RequestContext(user=other, guild=guild, key=SimpleNamespace(), roles=[])
            body = request_routes.StatusBody(version=req.version)
            with pytest.raises(HTTPException) as exc:
                await request_routes.confirm_request(req.id, body, ctx=ctx, db=db)
            return exc.value.status_code
    code = asyncio.run(run())
    assert code == 403


def test_cancel_requires_requester(db_setup):
    async def run():
        async with get_session() as db:
            guild = await db.get(Guild, 1)
            requester = await db.get(User, 1)
            assignee = await db.get(User, 2)
            other = await db.get(User, 3)
            req = DbRequest(
                id=4,
                guild_id=guild.id,
                user_id=requester.id,
                assignee_id=assignee.id,
                title="Test",
                type=RequestType.ITEM,
                status=RequestStatus.OPEN,
                urgency=Urgency.LOW,
            )
            db.add(req)
            await db.commit()
            ctx = RequestContext(user=other, guild=guild, key=SimpleNamespace(), roles=[])
            body = request_routes.StatusBody(version=req.version)
            with pytest.raises(HTTPException) as exc:
                await request_routes.cancel_request(req.id, body, ctx=ctx, db=db)
            return exc.value.status_code
    code = asyncio.run(run())
    assert code == 403


def test_cancel_returns_dto(db_setup):
    async def run():
        async with get_session() as db:
            guild = await db.get(Guild, 1)
            requester = await db.get(User, 1)
            req = DbRequest(
                id=5,
                guild_id=guild.id,
                user_id=requester.id,
                title="Test",
                type=RequestType.ITEM,
                status=RequestStatus.OPEN,
                urgency=Urgency.LOW,
            )
            db.add(req)
            await db.commit()
            ctx = RequestContext(user=requester, guild=guild, key=SimpleNamespace(), roles=[])
            old_version = req.version
            body = request_routes.StatusBody(version=old_version)
            dto = await request_routes.cancel_request(req.id, body, ctx=ctx, db=db)
            assert dto["status"] == RequestStatus.CANCELLED.value
            assert dto["version"] == old_version + 1
    asyncio.run(run())
