import json
import sys
import types
from dataclasses import dataclass
from pathlib import Path
from types import SimpleNamespace

import pytest
from fastapi.responses import JSONResponse

root = Path(__file__).resolve().parents[1] / "demibot"
if str(root) not in sys.path:
    sys.path.append(str(root))

# Stub the core package structure to avoid importing heavy optional
# dependencies during test discovery. The routes themselves continue to be
# loaded from the actual source files via the configured package paths.
demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

routes_pkg = types.ModuleType("demibot.http.routes")
routes_pkg.__path__ = [str(root / "demibot/http/routes")]
sys.modules.setdefault("demibot.http.routes", routes_pkg)

# Minimal stand-ins for auth dependencies required only for type hints.
deps_pkg = types.ModuleType("demibot.http.deps")


@dataclass
class _RequestContext:
    user: object
    guild: object
    key: object
    roles: list[str]


deps_pkg.RequestContext = _RequestContext


def _api_key_auth():  # pragma: no cover - FastAPI dependency placeholder
    raise NotImplementedError


deps_pkg.api_key_auth = _api_key_auth
sys.modules.setdefault("demibot.http.deps", deps_pkg)

# Provide lightweight stubs for discord.py so the route can be imported
# without pulling in the full dependency graph during tests.
discord_pkg = types.ModuleType("discord")
discord_ext_pkg = types.ModuleType("discord.ext")
discord_commands_pkg = types.ModuleType("discord.ext.commands")


class _DummyBot:
    pass


discord_commands_pkg.Bot = _DummyBot
discord_ext_pkg.commands = discord_commands_pkg
discord_pkg.ext = discord_ext_pkg

sys.modules.setdefault("discord", discord_pkg)
sys.modules.setdefault("discord.ext", discord_ext_pkg)
sys.modules.setdefault("discord.ext.commands", discord_commands_pkg)

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

    def is_ready(self):
        return True

    def is_closed(self):
        return False

    def get_guild(self, guild_id):
        return self._guild


@pytest.mark.asyncio
async def test_emojis_warmup_returns_empty_and_retry_after(monkeypatch):
    discord_emojis._emoji_cache.clear()

    class WarmupClient:
        def __init__(self):
            self.get_guild_called = False

        def is_ready(self):
            return False

        def is_closed(self):
            return False

        def get_guild(self, guild_id):
            self.get_guild_called = True
            return None

    client = WarmupClient()
    monkeypatch.setattr(discord_emojis, "discord_client", client)

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
    assert not client.get_guild_called


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


@pytest.mark.asyncio
async def test_emojis_returns_empty_list_when_guild_has_no_custom(monkeypatch):
    discord_emojis._emoji_cache.clear()

    guild = DummyGuild([])
    monkeypatch.setattr(discord_emojis, "discord_client", DummyClient(guild))

    ctx = RequestContext(
        user=SimpleNamespace(id=1),
        guild=SimpleNamespace(id=1, discord_guild_id=100),
        key=None,
        roles=[],
    )

    data = await discord_emojis.list_emojis(ctx=ctx)

    assert data == {"ok": True, "emojis": []}
    assert discord_emojis._emoji_cache[ctx.guild.id] == []

    # Cached copy should be used on subsequent calls without hitting Discord.
    cached = await discord_emojis.list_emojis(ctx=ctx)
    assert cached == data
