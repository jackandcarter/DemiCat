import sys
from pathlib import Path
import types

import pytest

root = Path(__file__).resolve().parents[1] / 'demibot'
sys.path.append(str(root))

demibot_pkg = types.ModuleType('demibot')
demibot_pkg.__path__ = [str(root / 'demibot')]
sys.modules.setdefault('demibot', demibot_pkg)
http_pkg = types.ModuleType('demibot.http')
http_pkg.__path__ = [str(root / 'demibot/http')]
sys.modules.setdefault('demibot.http', http_pkg)
routes_pkg = types.ModuleType('demibot.http.routes')
routes_pkg.__path__ = [str(root / 'demibot/http/routes')]
sys.modules.setdefault('demibot.http.routes', routes_pkg)

from demibot.http.routes import emojis


class DummyEmoji:
    def __init__(self, eid, name, animated, url):
        self.id = eid
        self.name = name
        self.animated = animated
        self.url = url


class DummyGuild:
    def __init__(self, emojis):
        self.emojis = emojis


class DummyClient:
    def __init__(self, guild, ready: bool = True, closed: bool = False):
        self.guild = guild
        self._ready = ready
        self._closed = closed
        self.calls = []

    def is_ready(self):
        return self._ready

    def is_closed(self):
        return self._closed

    def get_guild(self, guild_id):
        self.calls.append(guild_id)
        return self.guild


class StubCtx:
    def __init__(self):
        self.guild = types.SimpleNamespace(id=1, discord_guild_id=100)


def _expected_payload():
    return [
        {'id': '1', 'name': 'foo', 'isAnimated': False, 'imageUrl': 'url1'},
        {'id': '2', 'name': 'bar', 'isAnimated': True, 'imageUrl': 'url2'},
    ]


@pytest.mark.asyncio
async def test_get_emojis(monkeypatch):
    emojis._emoji_cache.clear()
    guild = DummyGuild([DummyEmoji(1, 'foo', False, 'url1'), DummyEmoji(2, 'bar', True, 'url2')])
    client = DummyClient(guild)
    monkeypatch.setattr(emojis, 'discord_client', client, raising=False)
    ctx = StubCtx()

    res1 = await emojis.get_emojis(ctx=ctx)
    assert res1 == _expected_payload()
    assert client.calls == [ctx.guild.discord_guild_id]

    monkeypatch.setattr(emojis, 'discord_client', None, raising=False)
    res2 = await emojis.get_emojis(ctx=ctx)
    assert res2 == res1

    emojis._emoji_cache.clear()
    monkeypatch.setattr(emojis, 'discord_client', client, raising=False)
    res3 = await emojis.get_guild_emojis(100, ctx=ctx)
    assert res3 == _expected_payload()


@pytest.mark.asyncio
async def test_emojis_return_empty_when_client_not_ready(monkeypatch):
    emojis._emoji_cache.clear()

    class NotReadyClient:
        def __init__(self):
            self.get_guild_called = False

        def is_ready(self):
            return False

        def is_closed(self):
            return False

        def get_guild(self, guild_id):
            self.get_guild_called = True
            raise AssertionError('get_guild should not be called while syncing')

    ctx = StubCtx()
    client = NotReadyClient()
    monkeypatch.setattr(emojis, 'discord_client', client, raising=False)

    assert await emojis.get_emojis(ctx=ctx) == []
    assert await emojis.get_guild_emojis(123, ctx=ctx) == []
    assert not client.get_guild_called
