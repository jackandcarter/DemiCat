import asyncio
import sys
import types
from pathlib import Path
from types import SimpleNamespace

from sqlalchemy import select

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

discordbot_pkg = types.ModuleType("demibot.discordbot")
discordbot_pkg.__path__ = [str(root / "demibot/discordbot")]
sys.modules.setdefault("demibot.discordbot", discordbot_pkg)

from demibot.db import session as db_session
from demibot.db.models import (
    Guild,
    GuildConfig,
    Membership,
    MembershipRole,
    Role,
    User,
    UserKey,
)
from demibot.db.session import get_session, init_db
from demibot.discordbot.cogs.presence import PresenceTracker
from demibot.http.deps import api_key_auth
from demibot.http.routes import validate_roles


class DummyMember:
    def __init__(self, guild, roles):
        self.id = 2
        self.guild = guild
        self.roles = roles
        self.display_name = "Member"
        self.name = "Member"
        self.display_avatar = SimpleNamespace(url="https://example.com/avatar.png")
        self.status = "online"
        self.activities: list = []
        self.global_name = "Member"
        self.discriminator = "0001"


def _reset_db(path: Path) -> None:
    if path.exists():
        path.unlink()
    db_session._engine = None
    db_session._Session = None


def test_presence_updates_membership_roles_for_api():
    async def _run():
        db_path = Path("presence_roles.db")
        _reset_db(db_path)
        url = f"sqlite+aiosqlite:///{db_path}"
        await init_db(url)

        async with get_session() as db:
            guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
            db.add(guild)
            db.add(
                GuildConfig(
                    guild_id=guild.id,
                    officer_role_ids="10",
                    mention_role_ids="20",
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

        tracker = PresenceTracker(bot=SimpleNamespace())
        guild_stub = SimpleNamespace(id=1, name="Test Guild")
        officer_role = SimpleNamespace(id=10, name="Officer")
        chat_role = SimpleNamespace(id=20, name="Chat")
        member = DummyMember(guild_stub, [officer_role])

        await tracker._update(member)

        async with get_session() as db:
            user = (
                await db.execute(select(User).where(User.discord_user_id == member.id))
            ).scalar_one()
            guild_row = (
                await db.execute(select(Guild).where(Guild.discord_guild_id == 1))
            ).scalar_one()
            db.add(UserKey(user_id=user.id, guild_id=guild_row.id, token="token"))
            await db.commit()
            user_id = user.id
            guild_id = guild_row.id

        async def fetch_flags():
            async with get_session() as db:
                ctx = await api_key_auth(x_api_key="token", x_discord_id=None, db=db)
                roles_resp = await validate_roles.roles(ctx=ctx)
                membership_role_ids = sorted(
                    (
                        await db.execute(
                            select(Role.discord_role_id)
                            .join(MembershipRole, MembershipRole.role_id == Role.id)
                            .join(
                                Membership,
                                MembershipRole.membership_id == Membership.id,
                            )
                            .where(
                                Membership.guild_id == guild_id,
                                Membership.user_id == user_id,
                            )
                        )
                    ).scalars().all()
                )
                return roles_resp.roles, membership_role_ids

        roles_flags, membership_role_ids = await fetch_flags()
        assert membership_role_ids == [10]
        assert set(roles_flags) == {"officer"}

        member.roles = [chat_role]
        await tracker._update(member)

        roles_flags, membership_role_ids = await fetch_flags()
        assert membership_role_ids == [20]
        assert set(roles_flags) == {"chat"}

        engine = db_session._engine
        if engine is not None:
            await engine.dispose()
        _reset_db(db_path)

    asyncio.run(_run())
