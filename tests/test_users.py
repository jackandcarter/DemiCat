
from pathlib import Path
import sys
import types
import pytest
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
discordbot_pkg = types.ModuleType('demibot.discordbot')
discordbot_pkg.__path__ = [str(root / 'demibot/discordbot')]
sys.modules.setdefault('demibot.discordbot', discordbot_pkg)

from demibot.http.routes.users import get_users
from demibot.discordbot.presence_store import set_presence, Presence as StorePresence
from demibot.db.models import (
    User,
    Membership,
    Presence as DbPresence,
    Role,
    MembershipRole,
)
from demibot.db.session import init_db, get_session
from sqlalchemy import delete


class StubContext:
    def __init__(self, guild_id: int):
        self.guild = types.SimpleNamespace(id=guild_id)
        self.roles = []


def test_get_users_includes_status_from_cache():
    async def _run():
        await init_db('sqlite+aiosqlite://')
        async with get_session() as db:
            await db.execute(delete(MembershipRole))
            await db.execute(delete(Role))
            await db.execute(delete(DbPresence))
            await db.execute(delete(Membership))
            await db.execute(delete(User))
            await db.commit()
            db.add(User(id=1, discord_user_id=10, global_name='Alice'))
            db.add(User(id=2, discord_user_id=20, global_name='Bob'))
            db.add(Role(id=1, guild_id=1, discord_role_id=100, name='Officer'))
            db.add(Membership(id=1, guild_id=1, user_id=1))
            db.add(Membership(id=2, guild_id=1, user_id=2))
            db.add(MembershipRole(membership_id=1, role_id=1))
            await db.commit()
            set_presence(1, StorePresence(id=10, name='Alice', status='online'))
            set_presence(1, StorePresence(id=20, name='Bob', status='offline'))
            ctx = StubContext(1)
            res = await get_users(ctx=ctx, db=db)
            assert {
                (u['id'], u['status'], tuple(u['roles'])) for u in res
            } == {('10', 'online', ('100',)), ('20', 'offline', ())}
            break
    asyncio.run(_run())


def test_get_users_reads_presence_from_db():
    async def _run():
        await init_db('sqlite+aiosqlite://')
        async with get_session() as db:
            await db.execute(delete(MembershipRole))
            await db.execute(delete(Role))
            await db.execute(delete(DbPresence))
            await db.execute(delete(Membership))
            await db.execute(delete(User))
            await db.commit()
            db.add(User(id=3, discord_user_id=30, global_name='Alice'))
            db.add(User(id=4, discord_user_id=40, global_name='Bob'))
            db.add(Membership(guild_id=1, user_id=3))
            db.add(Membership(guild_id=1, user_id=4))
            db.add(DbPresence(guild_id=1, user_id=30, status='online'))
            db.add(DbPresence(guild_id=1, user_id=40, status='offline'))
            await db.commit()
            ctx = StubContext(1)
            res = await get_users(ctx=ctx, db=db)
            assert {
                (u['id'], u['status'], tuple(u['roles'])) for u in res
            } == {('30', 'online', ()), ('40', 'offline', ())}
            break
    asyncio.run(_run())
