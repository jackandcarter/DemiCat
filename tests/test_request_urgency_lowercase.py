import asyncio

from fastapi.testclient import TestClient
from sqlalchemy import select

from demibot.http.api import create_app
from demibot.http.deps import RequestContext, api_key_auth
from demibot.db.session import get_session, init_db
from demibot.db.models import (
    Guild,
    Request as DbRequest,
    RequestStatus,
    Urgency,
    User,
)


def test_create_request_returns_lowercase_urgency(monkeypatch):
    user = User(id=1, discord_user_id=1)
    guild = Guild(id=1, discord_guild_id=1, name="G")

    async def _setup():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            await db.merge(user)
            await db.merge(guild)
            await db.commit()

    asyncio.run(_setup())

    app = create_app()
    app.dependency_overrides[api_key_auth] = lambda: RequestContext(
        user=user, guild=guild, key=None, roles=[]
    )

    async def _noop(*args, **kwargs):
        pass

    def _dto_stub(req):
        urgency = req.urgency.value if isinstance(req.urgency, Urgency) else req.urgency
        status = req.status.value if isinstance(req.status, RequestStatus) else req.status
        return {"id": str(req.id), "status": status, "urgency": urgency}

    monkeypatch.setattr("demibot.http.routes.requests._broadcast", _noop)
    monkeypatch.setattr("demibot.http.routes.requests._notify", _noop)
    monkeypatch.setattr("demibot.http.routes.requests._dto", _dto_stub)

    client = TestClient(app)

    created: dict[int, str] = {}
    for urgency in Urgency:
        resp = client.post(
            "/api/requests",
            json={
                "title": f"{urgency.value} request",
                "type": "item",
                "urgency": urgency.value,
            },
        )
        assert resp.status_code == 200

        request_id = int(resp.json()["id"])
        created[request_id] = urgency.value

        detail_resp = client.get(f"/api/requests/{request_id}")
        assert detail_resp.status_code == 200
        payload = detail_resp.json()
        assert payload["urgency"] == urgency.value
        assert payload["urgency"] == payload["urgency"].lower()
        assert payload["status"] == RequestStatus.OPEN.value

    created_ids = tuple(created.keys())

    async def _urgencies() -> list[tuple[int, str]]:
        if not created_ids:
            return []
        async with get_session() as db:
            rows = await db.execute(
                select(
                    DbRequest.__table__.c.id,
                    DbRequest.__table__.c.urgency,
                )
                .where(DbRequest.__table__.c.id.in_(created_ids))
                .order_by(DbRequest.__table__.c.id)
            )
            return rows.all()

    stored = asyncio.run(_urgencies())
    assert {request_id: urgency for request_id, urgency in stored} == created
    assert all(urgency == urgency.lower() for _, urgency in stored)
