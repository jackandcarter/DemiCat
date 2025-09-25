from __future__ import annotations

import sys
import types
from pathlib import Path

import pytest


root = Path(__file__).resolve().parents[1] / "demibot"
if str(root) not in sys.path:
    sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

discord_pkg = types.ModuleType("discord")
discord_ext_pkg = types.ModuleType("discord.ext")
discord_commands_pkg = types.ModuleType("discord.ext.commands")


class _DummyBot:
    def __init__(self) -> None:
        self.marker = "bot"


discord_commands_pkg.Bot = _DummyBot
discord_ext_pkg.commands = discord_commands_pkg
discord_pkg.ext = discord_ext_pkg

sys.modules.setdefault("discord", discord_pkg)
sys.modules.setdefault("discord.ext", discord_ext_pkg)
sys.modules.setdefault("discord.ext.commands", discord_commands_pkg)


def test_set_discord_client_updates_existing_imports():
    from demibot.http.discord_client import (
        discord_client,
        get_discord_client,
        set_discord_client,
    )

    assert bool(discord_client) is False
    assert get_discord_client() is None

    bot = _DummyBot()
    set_discord_client(bot)

    assert bool(discord_client) is True
    assert get_discord_client() is bot
    assert discord_client.marker == "bot"


def test_set_discord_client_accepts_none():
    from demibot.http.discord_client import (
        discord_client,
        get_discord_client,
        set_discord_client,
    )

    set_discord_client(None)

    assert get_discord_client() is None
    assert bool(discord_client) is False
    with pytest.raises(AttributeError):
        _ = discord_client.marker
