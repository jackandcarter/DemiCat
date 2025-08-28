import asyncio
import sys
import types
from pathlib import Path

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))
demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

from demibot.db.models import Guild, User, UserKey
from demibot.db.session import init_db, get_session
from demibot.http.deps import api_key_auth


def test_x_discord_id_overrides_user(tmp_path):
    db_path = tmp_path / "auth.db"
    asyncio.run(init_db(f"sqlite+aiosqlite:///{db_path}"))

    async def populate():
        async for db in get_session():
            guild = Guild(id=1, discord_guild_id=1, name="Test")
            svc = User(id=1, discord_user_id=10)
            user = User(id=2, discord_user_id=20)
            key = UserKey(user_id=svc.id, guild_id=guild.id, token="svc")
            db.add_all([guild, svc, user, key])
            await db.commit()
            break

    asyncio.run(populate())

    async def run():
        async for db in get_session():
            ctx = await api_key_auth(x_api_key="svc", x_discord_id=20, db=db)
            return ctx.user.id

    uid = asyncio.run(run())
    assert uid == 2
