import asyncio

from fastapi.testclient import TestClient
from sqlalchemy import select

from demibot.http.api import create_app
from demibot.http.deps import RequestContext, api_key_auth
from demibot.db.session import init_db, get_session
from demibot.db.models import Guild, Request as DbRequest, RequestStatus, User


def test_create_request_persists_lowercase_status(monkeypatch):
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

    monkeypatch.setattr("demibot.http.routes.requests._broadcast", _noop)
    monkeypatch.setattr("demibot.http.routes.requests._notify", _noop)
    monkeypatch.setattr("demibot.http.routes.requests._dto", lambda req: {"id": str(req.id)})

    client = TestClient(app)
    resp = client.post(
        "/api/requests", json={"title": "t", "type": "item", "urgency": "low"}
    )
    assert resp.status_code == 200

    async def _statuses():
        async with get_session() as db:
            rows = await db.execute(select(DbRequest.__table__.c.status))
            return [status for (status,) in rows.all()]

    statuses = asyncio.run(_statuses())

    assert statuses == [RequestStatus.OPEN.value]
    assert set(statuses) <= {status.value for status in RequestStatus}
