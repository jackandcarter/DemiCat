import asyncio
import sys
from pathlib import Path
from types import SimpleNamespace
import types

from sqlalchemy import select

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

discordbot_pkg = types.ModuleType("demibot.discordbot")
discordbot_pkg.__path__ = [str(root / "demibot/discordbot")]
sys.modules.setdefault("demibot.discordbot", discordbot_pkg)

from demibot.discordbot.cogs import admin as admin_module
from demibot.db.models import (
    Guild,
    Role,
    User,
    Membership,
    MembershipRole,
    UserKey,
)
from demibot.db.session import init_db, get_session


class DummyResponse:
    def __init__(self) -> None:
        self.args = ()
        self.kwargs: dict | None = None

    async def send_message(
        self, *args, **kwargs
    ) -> None:  # pragma: no cover - simple stub
        self.args = args
        self.kwargs = kwargs


class OwnerInteraction:
    def __init__(self) -> None:
        self.guild = SimpleNamespace(id=1, owner_id=1, name="Test Guild")
        perms = SimpleNamespace(administrator=False)
        self.user = SimpleNamespace(id=1, roles=[], guild_permissions=perms)
        self.response = DummyResponse()
        self.client = SimpleNamespace(cfg=None)


class ButtonInteraction:
    def __init__(self, user) -> None:
        self.guild = SimpleNamespace(id=1, name="Test Guild")
        self.user = user
        self.response = DummyResponse()


async def _setup_db() -> None:
    db_path = Path("test.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async for db in get_session():
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        db.add(guild)
        db.add(
            Role(
                id=10,
                guild_id=guild.id,
                name="Officer",
                is_officer=True,
                discord_role_id=10,
            )
        )
        db.add(
            User(
                id=1,
                discord_user_id=2,
                global_name="Member",
                discriminator="0001",
            )
        )
        await db.commit()
        break


async def _generate(user_roles):
    await _setup_db()

    owner_inter = OwnerInteraction()
    await admin_module.key_embed.callback(owner_inter)
    view = owner_inter.response.kwargs["view"]

    user = SimpleNamespace(
        id=2,
        global_name="Member",
        discriminator="0001",
        roles=user_roles,
    )
    button_inter = ButtonInteraction(user)
    await view.children[0].callback(button_inter)

    async for db in get_session():
        user_row = (
            await db.execute(select(User).where(User.discord_user_id == user.id))
        ).scalar_one()
        guild_row = (
            await db.execute(select(Guild).where(Guild.discord_guild_id == 1))
        ).scalar_one()
        membership = (
            await db.execute(
                select(Membership).where(
                    Membership.guild_id == guild_row.id,
                    Membership.user_id == user_row.id,
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
                select(UserKey).where(
                    UserKey.user_id == user_row.id,
                    UserKey.guild_id == guild_row.id,
                )
            )
        ).scalar_one()
        return membership_roles, key.roles_cached, button_inter.response


def test_non_officer_generates_key_and_no_roles():
    roles = [SimpleNamespace(id=99, name="Member")]
    membership_roles, cached, response = asyncio.run(_generate(roles))
    assert membership_roles == []
    assert cached == "99"
    assert response.args and "Your sync key" in response.args[0]


def test_officer_generates_key_and_role_classified():
    roles = [SimpleNamespace(id=10, name="Officer")]
    membership_roles, cached, response = asyncio.run(_generate(roles))
    assert membership_roles == [10]
    assert cached == "10"
    assert response.args and "Your sync key" in response.args[0]
