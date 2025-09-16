import json
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest
from fastapi.responses import JSONResponse

root = Path(__file__).resolve().parents[1] / "demibot"
if str(root) not in sys.path:
    sys.path.append(str(root))

from demibot.http.deps import RequestContext
from demibot.http.routes import discord_emojis


class DummyEmoji:
    def __init__(self, eid, name, animated):
        self.id = eid
        self.name = name
        self.animated = animated


class DummyGuild:
    def __init__(self, emojis):
        self.emojis = emojis


class DummyClient:
    def __init__(self, guild):
        self._guild = guild

    def get_guild(self, guild_id):
        return self._guild


@pytest.mark.asyncio
async def test_emojis_warmup_returns_empty_and_retry_after(monkeypatch):
    discord_emojis._emoji_cache.clear()

    class WarmupClient:
        def get_guild(self, guild_id):
            return None

    monkeypatch.setattr(discord_emojis, "discord_client", WarmupClient())

    ctx = RequestContext(
        user=SimpleNamespace(id=1),
        guild=SimpleNamespace(id=1, discord_guild_id=100),
        key=None,
        roles=[],
    )

    response = await discord_emojis.list_emojis(ctx=ctx)

    assert isinstance(response, JSONResponse)
    assert response.status_code == 200
    assert response.headers["Retry-After"] == str(
        discord_emojis._RETRY_AFTER_SECONDS
    )
    assert json.loads(response.body) == {"ok": True, "emojis": []}


@pytest.mark.asyncio
async def test_emojis_returns_list_when_ready(monkeypatch):
    discord_emojis._emoji_cache.clear()

    guild = DummyGuild(
        [DummyEmoji(1, "sparkle", False), DummyEmoji(2, "dance", True)]
    )
    monkeypatch.setattr(discord_emojis, "discord_client", DummyClient(guild))

    ctx = RequestContext(
        user=SimpleNamespace(id=1),
        guild=SimpleNamespace(id=1, discord_guild_id=100),
        key=None,
        roles=[],
    )

    data = await discord_emojis.list_emojis(ctx=ctx)

    assert data == {
        "ok": True,
        "emojis": [
            {"id": "1", "name": "sparkle", "animated": False},
            {"id": "2", "name": "dance", "animated": True},
        ],
    }
    # Cached copy should now be available for subsequent calls.
    assert discord_emojis._emoji_cache[ctx.guild.id] == data["emojis"]
