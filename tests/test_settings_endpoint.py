import pytest
from httpx import AsyncClient
from demibot.http.api import create_app

@pytest.mark.asyncio
async def test_settings_endpoint():
    app = create_app()
    async with AsyncClient(app=app, base_url="http://test") as ac:
        resp = await ac.get("/api/settings")
    assert resp.status_code == 200
    data = resp.json()
    assert data["syncedChat"] is True
    assert data["fcSyncShell"] is False
