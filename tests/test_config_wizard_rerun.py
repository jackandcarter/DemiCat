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

discord_module = types.ModuleType("discord")


class _DummyButtonStyle:
    secondary = 1
    primary = 2
    success = 3


class _DummyEmbed:
    def __init__(self, *args, **kwargs):
        pass

    def set_image(self, *args, **kwargs):
        pass


class _DummyHTTPException(Exception):
    pass


class _DummyView:
    def __init__(self, timeout: int | None = None) -> None:
        self.timeout = timeout
        self.children: list[object] = []

    def clear_items(self) -> None:
        self.children.clear()

    def add_item(self, item: object) -> None:
        self.children.append(item)

    def stop(self) -> None:
        pass


class _DummyButton:
    def __init__(self, *, label: str | None = None, style: int | None = None):
        self.label = label
        self.style = style
        self.callback = None
        self.disabled = False


class _DummyRoleSelect:
    def __init__(
        self,
        *,
        placeholder: str | None = None,
        min_values: int = 0,
        max_values: int = 1,
        default_values: list[object] | None = None,
    ) -> None:
        self.placeholder = placeholder
        self.min_values = min_values
        self.max_values = max_values
        self.values = default_values or []
        self.callback = None


def _dummy_button_decorator(*args, **kwargs):
    def decorator(func):
        return func

    return decorator


ui_module = types.ModuleType("discord.ui")
ui_module.View = _DummyView
ui_module.Button = _DummyButton
ui_module.RoleSelect = _DummyRoleSelect
ui_module.button = _dummy_button_decorator


class _DummyGroup:
    def __init__(self, *args, **kwargs):
        pass

    def command(self, *args, **kwargs):
        def decorator(func):
            return func

        return decorator


def _dummy_describe(**kwargs):
    def decorator(func):
        return func

    return decorator


app_commands_module = types.ModuleType("discord.app_commands")
app_commands_module.Group = _DummyGroup
app_commands_module.describe = _dummy_describe


commands_module = types.ModuleType("discord.ext.commands")


class _DummyCog:
    pass


class _DummyBot:
    pass


commands_module.Cog = _DummyCog
commands_module.Bot = _DummyBot

ext_module = types.ModuleType("discord.ext")
ext_module.commands = commands_module

discord_module.ButtonStyle = _DummyButtonStyle
discord_module.Embed = _DummyEmbed
discord_module.HTTPException = _DummyHTTPException
discord_module.ui = ui_module
discord_module.app_commands = app_commands_module

sys.modules.setdefault("discord", discord_module)
sys.modules.setdefault("discord.ui", ui_module)
sys.modules.setdefault("discord.app_commands", app_commands_module)
sys.modules.setdefault("discord.ext", ext_module)
sys.modules.setdefault("discord.ext.commands", commands_module)

from sqlalchemy import select

from demibot.discordbot.cogs.admin import ConfigWizard
from demibot.db.models import Guild, GuildChannel, ChannelKind
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
    def __init__(self, guild_id: int = 1) -> None:
        self.id = guild_id
        self.name = "Test Guild"
        self.text_channels = [
            DummyChannel(1, "one"),
            DummyChannel(2, "two"),
            DummyChannel(3, "three"),
            DummyChannel(4, "four"),
            DummyChannel(5, "five"),
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

        async with get_session() as db:
            guild_chan = (await db.execute(select(GuildChannel))).scalars().first()
            assert guild_chan is not None
            db.add(
                GuildChannel(
                    guild_id=guild_chan.guild_id,
                    channel_id=5,
                    kind=ChannelKind.CHAT,
                    name="five",
                    webhook_url="https://example.com/hook",
                )
            )
            await db.commit()

        # Second run of the wizard should succeed without IntegrityError
        view2 = ConfigWizard(guild, "title", "final", "done")
        # Change only the FC chat selection to ensure the previous FC
        # channel rows are replaced cleanly while leaving other kinds intact.
        view2.event_channel_ids = [1]
        view2.fc_chat_channel_ids = [5]
        view2.officer_chat_channel_ids = [3]
        view2.officer_role_ids = [42]
        view2.mention_role_ids = [42]
        await view2.on_finish(DummyInteraction())

        async with get_session() as db:
            chans = (
                await db.execute(
                    select(GuildChannel).order_by(
                        GuildChannel.channel_id, GuildChannel.kind
                    )
                )
            ).scalars().all()
            assert len(chans) == 3
            fc_channels = [
                chan.channel_id for chan in chans if chan.kind == ChannelKind.FC_CHAT
            ]
            assert fc_channels == [5]
            fc_entry = next(
                chan
                for chan in chans
                if chan.channel_id == 5 and chan.kind == ChannelKind.FC_CHAT
            )
            assert fc_entry.webhook_url == "https://example.com/hook"
            channel_map = {
                (chan.channel_id, chan.kind): chan.name for chan in chans
            }
            assert channel_map == {
                (1, ChannelKind.EVENT): "one",
                (3, ChannelKind.OFFICER_CHAT): "three",
                (5, ChannelKind.FC_CHAT): "five",
            }

    asyncio.run(_run())


def test_second_wizard_run_preserves_existing_webhook() -> None:
    async def _run():
        db_path = Path("test_rerun_wizard.db")
        await init_db(f"sqlite+aiosqlite:///{db_path}")

        guild = DummyGuild(2)
        guild.text_channels = [
            DummyChannel(101, "one"),
            DummyChannel(202, "two"),
            DummyChannel(303, "three"),
            DummyChannel(404, "four"),
            DummyChannel(505, "five"),
        ]

        view1 = ConfigWizard(guild, "title", "final", "done")
        view1.event_channel_ids = [101]
        view1.fc_chat_channel_ids = [202]
        view1.officer_chat_channel_ids = [303]
        view1.officer_role_ids = [84]
        view1.mention_role_ids = [84]
        await view1.on_finish(DummyInteraction())

        webhook_url = "https://example.com/original"
        guild_db_id = None
        async with get_session() as db:
            fc_row = await db.scalar(
                select(GuildChannel).where(
                    GuildChannel.channel_id == 202,
                    GuildChannel.kind == ChannelKind.FC_CHAT,
                )
            )
            assert fc_row is not None
            fc_row.webhook_url = webhook_url
            guild_db_id = fc_row.guild_id
            await db.commit()

        assert guild_db_id is not None

        view2 = ConfigWizard(guild, "title", "final", "done")
        view2.event_channel_ids = [101]
        view2.fc_chat_channel_ids = [202]
        view2.officer_chat_channel_ids = [303]
        view2.officer_role_ids = [84]
        view2.mention_role_ids = [84]
        await view2.on_finish(DummyInteraction())

        async with get_session() as db:
            rows = (
                await db.execute(
                    select(GuildChannel)
                    .where(GuildChannel.guild_id == guild_db_id)
                    .order_by(GuildChannel.channel_id, GuildChannel.kind)
                )
            ).scalars().all()

            fc_rows = [
                row
                for row in rows
                if row.channel_id == 202 and row.kind == ChannelKind.FC_CHAT
            ]
            assert len(fc_rows) == 1
            assert fc_rows[0].webhook_url == webhook_url
            channel_ids = [row.channel_id for row in rows]
            assert len(channel_ids) == len(set(channel_ids))

    asyncio.run(_run())


def test_existing_chat_channel_promoted_to_fc_chat() -> None:
    async def _run():
        db_path = Path("test_rerun_wizard.db")
        await init_db(f"sqlite+aiosqlite:///{db_path}")

        guild = DummyGuild(3)
        guild.text_channels = [
            DummyChannel(1001, "one"),
            DummyChannel(2002, "two"),
            DummyChannel(3003, "three"),
            DummyChannel(4004, "four"),
            DummyChannel(5005, "five"),
        ]
        fc_channel_id = 2002
        webhook_url = "https://example.com/chat-webhook"
        guild_db_id = None

        async with get_session() as db:
            guild_row = await db.scalar(
                select(Guild).where(Guild.discord_guild_id == guild.id)
            )
            if guild_row is None:
                guild_row = Guild(discord_guild_id=guild.id, name=guild.name)
                db.add(guild_row)
                await db.flush()

            guild_db_id = guild_row.id

            existing_channel = await db.scalar(
                select(GuildChannel).where(
                    GuildChannel.guild_id == guild_db_id,
                    GuildChannel.channel_id == fc_channel_id,
                )
            )
            if existing_channel is None:
                existing_channel = GuildChannel(
                    guild_id=guild_db_id,
                    channel_id=fc_channel_id,
                    kind=ChannelKind.CHAT,
                    name="two",
                    webhook_url=webhook_url,
                )
                db.add(existing_channel)
            else:
                existing_channel.kind = ChannelKind.CHAT
                existing_channel.name = "two"
                existing_channel.webhook_url = webhook_url

            await db.commit()

        assert guild_db_id is not None

        view = ConfigWizard(guild, "title", "final", "done")
        view.event_channel_ids = [1001]
        view.fc_chat_channel_ids = [fc_channel_id]
        view.officer_chat_channel_ids = [3003]
        view.officer_role_ids = [21]
        view.mention_role_ids = [21]
        await view.on_finish(DummyInteraction())

        async with get_session() as db:
            rows = (
                await db.execute(
                    select(GuildChannel)
                    .where(
                        GuildChannel.guild_id == guild_db_id,
                        GuildChannel.channel_id == fc_channel_id,
                    )
                    .order_by(GuildChannel.kind)
                )
            ).scalars().all()

            assert len(rows) == 1
            fc_row = rows[0]
            assert fc_row.kind == ChannelKind.FC_CHAT
            assert fc_row.webhook_url == webhook_url

    asyncio.run(_run())
