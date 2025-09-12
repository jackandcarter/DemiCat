import asyncio

from fastapi.testclient import TestClient
from sqlalchemy import select

from demibot.http.api import create_app
from demibot.http.deps import RequestContext, api_key_auth
from demibot.db.session import init_db, get_session
from demibot.db.models import Guild, Request as DbRequest, User


def test_create_request_accepts_lowercase_types(monkeypatch):
    user = User(id=1, discord_user_id=1)
    guild = Guild(id=1, discord_guild_id=1, name="G")

    async def _setup():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            db.add_all([user, guild])
            await db.commit()

    asyncio.run(_setup())

    app = create_app()
    app.dependency_overrides[api_key_auth] = lambda: RequestContext(
        user=user, guild=guild, key=None, roles=[]
    )

    async def _noop(*args, **kwargs):
        pass

    monkeypatch.setattr("demibot.http.routes.requests._notify", _noop)
    monkeypatch.setattr("demibot.http.routes.requests._dto", lambda req: {"id": str(req.id)})

    client = TestClient(app)

    for kind in ["item", "run", "event"]:
        resp = client.post(
            "/api/requests", json={"title": "t", "type": kind, "urgency": "low"}
        )
        assert resp.status_code == 200

    async def _types():
        async with get_session() as db:
            rows = await db.execute(select(DbRequest.type))
            return [t.value for (t,) in rows.all()]

    types = asyncio.run(_types())
    assert set(types) == {"item", "run", "event"}

