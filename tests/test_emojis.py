import sys
from pathlib import Path
import types
import asyncio

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
    def __init__(self, guild):
        self.guild = guild

    def get_guild(self, guild_id):
        return self.guild


class StubCtx:
    def __init__(self):
        self.guild = types.SimpleNamespace(id=1, discord_guild_id=100)


async def _run_test():
    emojis._emoji_cache.clear()
    guild = DummyGuild([DummyEmoji(1, 'foo', False, 'url1'), DummyEmoji(2, 'bar', True, 'url2')])
    emojis.discord_client = DummyClient(guild)
    ctx = StubCtx()
    res1 = await emojis.get_emojis(ctx=ctx)
    assert res1 == [
        {'id': '1', 'name': 'foo', 'isAnimated': False, 'imageUrl': 'url1'},
        {'id': '2', 'name': 'bar', 'isAnimated': True, 'imageUrl': 'url2'},
    ]
    emojis.discord_client = None
    res2 = await emojis.get_emojis(ctx=ctx)
    assert res2 == res1


def test_get_emojis():
    asyncio.run(_run_test())
