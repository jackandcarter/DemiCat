import types
import sys
import asyncio
import pytest
from fastapi import HTTPException
from pathlib import Path

# Ensure demibot package is importable without running full package init
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

# Provide a minimal stub for the discord module used by the routes
if 'discord' not in sys.modules:
    discord_mod = types.ModuleType('discord')
    abc_mod = types.ModuleType('discord.abc')

    class Messageable:
        pass

    class Forbidden(Exception):
        pass

    class NotFound(Exception):
        pass

    abc_mod.Messageable = Messageable
    discord_mod.abc = abc_mod
    discord_mod.Forbidden = Forbidden
    discord_mod.NotFound = NotFound
    ext_mod = types.ModuleType('discord.ext')
    commands_mod = types.ModuleType('discord.ext.commands')
    discord_mod.ext = ext_mod
    sys.modules['discord'] = discord_mod
    sys.modules['discord.abc'] = abc_mod
    sys.modules['discord.ext'] = ext_mod
    sys.modules['discord.ext.commands'] = commands_mod
else:
    discord_mod = sys.modules['discord']

from demibot.db.session import init_db, get_session
from demibot.db.models import Guild, User, Message
from demibot.http.deps import RequestContext
from demibot.http.routes import messages
from demibot.db import session as session_module


class DummyMessage:
    def __init__(self, raise_forbidden=False):
        self.called = False
        self._raise_forbidden = raise_forbidden

    async def add_reaction(self, emoji):
        if self._raise_forbidden:
            raise discord_mod.Forbidden()
        self.called = True


class DummyChannel(discord_mod.abc.Messageable):
    def __init__(self, msg=None, raise_not_found=False):
        self._msg = msg or DummyMessage()
        self._raise_not_found = raise_not_found

    async def fetch_message(self, mid):
        if self._raise_not_found:
            raise discord_mod.NotFound()
        return self._msg


def test_add_reaction_success():
    async def run():
        db_path = Path('test_reactions_success.db')
        if db_path.exists():
            db_path.unlink()
        session_module._engine = None
        session_module._Session = None
        await init_db(f"sqlite+aiosqlite:///{db_path}")
        async for db in get_session():
            db.add(Guild(id=1, discord_guild_id=1, name="Guild"))
            db.add(User(id=1, discord_user_id=10, global_name="Alice"))
            db.add(
                Message(
                    discord_message_id=1,
                    channel_id=1,
                    guild_id=1,
                    author_id=1,
                    author_name="Alice",
                    content_raw="",
                    content_display="",
                )
            )
            await db.commit()
            guild = await db.get(Guild, 1)
            user = await db.get(User, 1)
            ctx = RequestContext(user=user, guild=guild, key=object(), roles=["chat"])
            dummy_msg = DummyMessage()
            dummy_channel = DummyChannel(msg=dummy_msg)
            messages.discord_client = types.SimpleNamespace(get_channel=lambda _: dummy_channel)
            res = await messages.add_reaction("1", "1", "😀", ctx, db)
            assert res == {"ok": True}
            assert dummy_msg.called
            break
    asyncio.run(run())


def test_add_reaction_not_found():
    async def run():
        db_path = Path('test_reactions_not_found.db')
        if db_path.exists():
            db_path.unlink()
        session_module._engine = None
        session_module._Session = None
        await init_db(f"sqlite+aiosqlite:///{db_path}")
        async for db in get_session():
            db.add(Guild(id=1, discord_guild_id=1, name="Guild"))
            db.add(User(id=1, discord_user_id=10, global_name="Alice"))
            db.add(
                Message(
                    discord_message_id=1,
                    channel_id=1,
                    guild_id=1,
                    author_id=1,
                    author_name="Alice",
                    content_raw="",
                    content_display="",
                )
            )
            await db.commit()
            guild = await db.get(Guild, 1)
            user = await db.get(User, 1)
            ctx = RequestContext(user=user, guild=guild, key=object(), roles=["chat"])
            dummy_channel = DummyChannel(raise_not_found=True)
            messages.discord_client = types.SimpleNamespace(get_channel=lambda _: dummy_channel)
            with pytest.raises(HTTPException) as exc:
                await messages.add_reaction("1", "1", "😀", ctx, db)
            assert exc.value.status_code == 404
            break
    asyncio.run(run())


def test_add_reaction_forbidden():
    async def run():
        db_path = Path('test_reactions_forbidden.db')
        if db_path.exists():
            db_path.unlink()
        session_module._engine = None
        session_module._Session = None
        await init_db(f"sqlite+aiosqlite:///{db_path}")
        async for db in get_session():
            db.add(Guild(id=1, discord_guild_id=1, name="Guild"))
            db.add(User(id=1, discord_user_id=10, global_name="Alice"))
            db.add(
                Message(
                    discord_message_id=1,
                    channel_id=1,
                    guild_id=1,
                    author_id=1,
                    author_name="Alice",
                    content_raw="",
                    content_display="",
                )
            )
            await db.commit()
            guild = await db.get(Guild, 1)
            user = await db.get(User, 1)
            ctx = RequestContext(user=user, guild=guild, key=object(), roles=["chat"])
            dummy_msg = DummyMessage(raise_forbidden=True)
            dummy_channel = DummyChannel(msg=dummy_msg)
            messages.discord_client = types.SimpleNamespace(get_channel=lambda _: dummy_channel)
            with pytest.raises(HTTPException) as exc:
                await messages.add_reaction("1", "1", "😀", ctx, db)
            assert exc.value.status_code == 403
            break
    asyncio.run(run())
