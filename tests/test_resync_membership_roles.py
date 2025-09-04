import asyncio
from pathlib import Path
from types import SimpleNamespace

import asyncio
from pathlib import Path
from types import SimpleNamespace

from sqlalchemy import select

from demibot.discordbot.cogs import admin as admin_module
from demibot.db.models import Guild, Role, User, UserKey, Membership, MembershipRole
from demibot.db.session import init_db, get_session
from demibot.http.deps import api_key_auth
from demibot.http.routes import validate_roles


class DummyResponse:
    def __init__(self) -> None:
        self.kwargs: dict | None = None

    async def send_message(
        self, *args, **kwargs
    ) -> None:  # pragma: no cover - simple stub
        self.kwargs = kwargs


class DummyInteraction:
    def __init__(self, member):
        guild = SimpleNamespace(id=1, name="Test Guild")
        guild.get_member = lambda _id: member if _id == member.id else None
        self.guild = guild
        perms = SimpleNamespace(administrator=True)
        self.user = SimpleNamespace(id=99, guild_permissions=perms)
        self.response = DummyResponse()


def _setup_db() -> None:
    db_path = Path("test.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    asyncio.run(init_db(url))

    async def populate():
        async with get_session() as db:
            guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
            db.add(guild)
            db.add(
                Role(
                    id=20,
                    guild_id=guild.id,
                    name="Officer",
                    discord_role_id=10,
                    is_officer=True,
                )
            )
            user = User(
                id=1, discord_user_id=2, global_name="Member", discriminator="0001"
            )
            db.add(user)
            db.add(
                UserKey(
                    user_id=user.id, guild_id=guild.id, token="abc", roles_cached=""
                )
            )
            await db.commit()

    asyncio.run(populate())


def test_resync_rebuilds_membership_and_roles():
    _setup_db()
    member = SimpleNamespace(id=2, roles=[SimpleNamespace(id=10, name="Officer")])
    inter = DummyInteraction(member)
    asyncio.run(admin_module.resync_members.callback(inter))

    async def check():
        async with get_session() as db:
            membership = (
                await db.execute(
                    select(Membership).where(
                        Membership.user_id == 1, Membership.guild_id == 1
                    )
                )
            ).scalar_one()
            membership_roles = (
                (
                    await db.execute(
                        select(MembershipRole.role_id).where(
                            MembershipRole.membership_id == membership.id
                        )
                    )
                )
                .scalars()
                .all()
            )
            key = (
                await db.execute(
                    select(UserKey).where(UserKey.user_id == 1, UserKey.guild_id == 1)
                )
            ).scalar_one()
            ctx = await api_key_auth(x_api_key=key.token, db=db)
            roles_resp = await validate_roles.roles(ctx)
            return membership_roles, key.roles_cached, roles_resp.roles

    roles, cached, http_roles = asyncio.run(check())
    assert roles == [20]
    assert cached == "10"
    assert "officer" in http_roles
