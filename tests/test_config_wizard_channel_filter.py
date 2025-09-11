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
    str(Path(__file__).resolve().parents[1] / "demibot" / "demibot" / "discordbot")
]
sys.modules.setdefault("demibot.discordbot", discordbot_module)

import discord

from demibot.discordbot.cogs.admin import ConfigWizard


class DummyChannel:
    def __init__(self, cid: int, name: str) -> None:
        self.id = cid
        self.name = name


class DummyGuild:
    def __init__(self) -> None:
        self.text_channels = [
            DummyChannel(1, "one"),
            DummyChannel(2, "two"),
            DummyChannel(3, "three"),
        ]

    def get_role(self, _rid: int):
        return None


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


def _channel_labels(view: ConfigWizard):
    return {
        item.label
        for item in view.children
        if isinstance(item, discord.ui.Button)
        and item.label in {"one", "two", "three"}
    }


def test_selected_channels_are_hidden_across_steps():
    async def _run():
        guild = DummyGuild()
        view = ConfigWizard(guild, "title", "final", "done")

        await view.render(DummyInteraction(), initial=True)
        assert _channel_labels(view) == {"one", "two", "three"}

        view.event_channel_ids = [1]
        view.step = 1
        await view.render(DummyInteraction(), initial=True)
        assert _channel_labels(view) == {"two", "three"}

        view.fc_chat_channel_ids = [2]
        view.step = 2
        await view.render(DummyInteraction(), initial=True)
        assert _channel_labels(view) == {"three"}

    asyncio.run(_run())
