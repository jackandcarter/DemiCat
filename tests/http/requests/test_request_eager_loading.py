import sys
from pathlib import Path

import pytest
from httpx import ASGITransport, AsyncClient

root = Path(__file__).resolve().parents[3] / "demibot"
if str(root) not in sys.path:
    sys.path.append(str(root))

from demibot.db.models import Guild, User
from demibot.db.session import get_session, init_db
from demibot.http.api import create_app
from demibot.http.deps import RequestContext, api_key_auth, get_db


@pytest.mark.asyncio
async def test_create_request_eager_loads(monkeypatch):
    await init_db("sqlite+aiosqlite://")

    user = User(id=1, discord_user_id=1)
    guild = Guild(id=1, discord_guild_id=1, name="Guild")

    async with get_session() as db:
        await db.merge(user)
        await db.merge(guild)
        await db.commit()

    async def _noop(*args, **kwargs):
        return None

    monkeypatch.setattr("demibot.http.routes.requests._broadcast", _noop)
    monkeypatch.setattr("demibot.http.routes.requests._notify", _noop)

    app = create_app()

    async def override_auth():
        return RequestContext(user=user, guild=guild, key=None, roles=[])

    async def override_db():
        async with get_session() as session:
            yield session

    app.dependency_overrides[api_key_auth] = override_auth
    app.dependency_overrides[get_db] = override_db

    transport = ASGITransport(app=app)
    async with AsyncClient(transport=transport, base_url="http://test") as client:
        resp = await client.post(
            "/api/requests",
            json={
                "title": "Gather carrots",
                "type": "item",
                "urgency": "low",
            },
        )

    app.dependency_overrides.clear()

    assert resp.status_code == 200
    assert "MissingGreenlet" not in resp.text
    payload = resp.json()
    assert payload["id"].isdigit()
