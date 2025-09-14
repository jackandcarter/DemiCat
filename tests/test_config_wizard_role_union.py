import asyncio
from types import SimpleNamespace
from pathlib import Path
import sys
import types


sys.path.append(str(Path(__file__).resolve().parents[1] / "demibot"))
sys.modules.setdefault("structlog", types.ModuleType("structlog"))
alembic_stub = types.ModuleType("alembic")
alembic_stub.command = types.SimpleNamespace()
alembic_config_stub = types.ModuleType("alembic.config")
alembic_config_stub.Config = object
sys.modules.setdefault("alembic", alembic_stub)
sys.modules.setdefault("alembic.command", alembic_stub.command)
sys.modules.setdefault("alembic.config", alembic_config_stub)
discordbot_module = types.ModuleType("demibot.discordbot")
discordbot_module.__path__ = [
    str(Path(__file__).resolve().parents[1] / "demibot" / "demibot" / "discordbot"),
]
sys.modules.setdefault("demibot.discordbot", discordbot_module)

from sqlalchemy import select

from demibot.discordbot.cogs.admin import ConfigWizard
from demibot.db.models import Role
from demibot.db.session import init_db, get_session


class DummyChannel:
    def __init__(self, cid: int, name: str) -> None:
        self.id = cid
        self.name = name


class DummyRole:
    def __init__(self, rid: int, name: str) -> None:
        self.id = rid
        self.name = name


class DummyGuild:
    def __init__(self) -> None:
        self.id = 1
        self.name = "Test Guild"
        self.text_channels = [
            DummyChannel(1, "one"),
            DummyChannel(2, "two"),
            DummyChannel(3, "three"),
        ]

    def get_role(self, rid: int):
        return DummyRole(rid, f"Role {rid}")


class DummyResponse:
    async def send_message(self, *args, **kwargs):
        pass

    async def edit_message(self, *args, **kwargs):
        pass


class DummyFollowup:
    async def edit_message(self, *args, **kwargs):
        pass


class DummyInteraction:
    def __init__(self) -> None:
        self.response = DummyResponse()
        self.followup = DummyFollowup()
        self.message = SimpleNamespace(id=0)


def test_role_union_deduplication() -> None:
    async def _run():
        db_path = Path("test_role_union.db")
        if db_path.exists():
            db_path.unlink()
        await init_db(f"sqlite+aiosqlite:///{db_path}")

        guild = DummyGuild()
        view = ConfigWizard(guild, "title", "final", "done")
        view.event_channel_ids = [1]
        view.fc_chat_channel_ids = [2]
        view.officer_chat_channel_ids = [3]
        view.officer_role_ids = [42]
        view.mention_role_ids = [42]

        await view.on_finish(DummyInteraction())

        async with get_session() as db:
            roles = (await db.execute(select(Role))).scalars().all()
            assert len(roles) == 1
            role = roles[0]
            assert role.discord_role_id == 42
            assert role.is_officer is True
            assert role.is_chat is True

    asyncio.run(_run())

