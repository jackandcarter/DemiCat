import asyncio
import sys
from pathlib import Path

from fastapi.testclient import TestClient

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

from demibot.db.models import Guild, User
from demibot.db.session import init_db
import demibot.db.session as db_session
from demibot.http.api import create_app
from demibot.http.deps import RequestContext, api_key_auth


async def _setup_db() -> None:
    db_session._engine = None
    db_session._Session = None
    await init_db("sqlite+aiosqlite://")


def test_rsvp_unknown_event_returns_404() -> None:
    asyncio.run(_setup_db())

    user = User(id=1, discord_user_id=1)
    guild = Guild(id=1, discord_guild_id=1, name="Guild")

    app = create_app()
    app.dependency_overrides[api_key_auth] = lambda: RequestContext(
        user=user,
        guild=guild,
        key=None,
        roles=[],
    )

    client = TestClient(app)
    response = client.post("/api/events/999999/rsvp", json={"tag": "join"})

    assert response.status_code == 404
