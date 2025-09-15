import asyncio
from pathlib import Path
import sys
import types

# Setup module stubs and path similar to other tests
sys.path.append(str(Path(__file__).resolve().parents[1] / "demibot"))
structlog_stub = types.ModuleType("structlog")
structlog_stub.get_logger = lambda *a, **k: None
sys.modules.setdefault("structlog", structlog_stub)
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
from demibot.db.models import GuildChannel, ChannelKind
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


class DummyMessage:
    def __init__(self) -> None:
        self.id = 0

    async def delete(self) -> None:
        pass


class DummyInteraction:
    def __init__(self) -> None:
        self.response = DummyResponse()
        self.followup = DummyFollowup()
        self.message = DummyMessage()


def test_rerun_setup_wizard_no_integrity_error() -> None:
    async def _run():
        db_path = Path("test_rerun_wizard.db")
        if db_path.exists():
            db_path.unlink()
        await init_db(f"sqlite+aiosqlite:///{db_path}")

        guild = DummyGuild()

        # First run of the wizard
        view1 = ConfigWizard(guild, "title", "final", "done")
        view1.event_channel_ids = [1]
        view1.fc_chat_channel_ids = [2]
        view1.officer_chat_channel_ids = [3]
        view1.officer_role_ids = [42]
        view1.mention_role_ids = [42]
        await view1.on_finish(DummyInteraction())

        # Change the existing channel mapping to a different kind to simulate a conflict
        async with get_session() as db:
            chan = (
                await db.execute(
                    select(GuildChannel).where(
                        GuildChannel.guild_id == 1, GuildChannel.channel_id == 1
                    )
                )
            ).scalar_one()
            chan.kind = ChannelKind.CHAT
            await db.commit()

        # Second run of the wizard should succeed without IntegrityError
        view2 = ConfigWizard(guild, "title", "final", "done")
        view2.event_channel_ids = [1]
        view2.fc_chat_channel_ids = [2]
        view2.officer_chat_channel_ids = [3]
        view2.officer_role_ids = [42]
        view2.mention_role_ids = [42]
        await view2.on_finish(DummyInteraction())

        async with get_session() as db:
            chans = (await db.execute(select(GuildChannel))).scalars().all()
            assert len(chans) == 3

    asyncio.run(_run())
