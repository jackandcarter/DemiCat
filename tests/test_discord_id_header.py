import asyncio
import sys
import types
from pathlib import Path

import pytest
from fastapi import HTTPException, status

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))
demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

from demibot.db.models import (
    Guild,
    Membership,
    MembershipRole,
    Role,
    User,
    UserKey,
)
from demibot.db.session import get_session, init_db
from demibot.http.deps import api_key_auth
from demibot.db import session as db_session


def test_x_discord_id_overrides_user(tmp_path):
    db_session._engine = None
    db_session._Session = None
    db_path = tmp_path / "auth.db"
    asyncio.run(init_db(f"sqlite+aiosqlite:///{db_path}"))

    async def populate():
        async for db in get_session():
            guild = Guild(id=1, discord_guild_id=1, name="Test")
            svc = User(id=1, discord_user_id=10)
            user = User(id=2, discord_user_id=20)
            officer = Role(
                id=1,
                guild_id=guild.id,
                discord_role_id=1,
                name="Officer",
                is_officer=True,
            )
            svc_membership = Membership(id=1, guild_id=guild.id, user_id=svc.id)
            svc_role = MembershipRole(
                membership_id=svc_membership.id, role_id=officer.id
            )
            key = UserKey(user_id=svc.id, guild_id=guild.id, token="svc")
            db.add_all(
                [guild, svc, user, officer, svc_membership, svc_role, key]
            )
            await db.commit()
            break

    asyncio.run(populate())

    async def run():
        async for db in get_session():
            ctx = await api_key_auth(x_api_key="svc", x_discord_id=20, db=db)
            return ctx.user.id

    uid = asyncio.run(run())
    assert uid == 2


def test_x_discord_id_requires_officer(tmp_path):
    db_session._engine = None
    db_session._Session = None
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
            await api_key_auth(x_api_key="svc", x_discord_id=20, db=db)

    with pytest.raises(HTTPException) as exc:
        asyncio.run(run())
    assert exc.value.status_code == status.HTTP_403_FORBIDDEN
