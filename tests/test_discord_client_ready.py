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
    pass


discord_commands_pkg.Bot = _DummyBot
discord_ext_pkg.commands = discord_commands_pkg
discord_pkg.ext = discord_ext_pkg

sys.modules.setdefault("discord", discord_pkg)
sys.modules.setdefault("discord.ext", discord_ext_pkg)
sys.modules.setdefault("discord.ext.commands", discord_commands_pkg)

from demibot.http.discord_client import is_discord_client_ready


class _ReadyMethodClient:
    def is_closed(self):
        return False

    def is_ready(self):
        return True


class _NotReadyMethodClient:
    def is_closed(self):
        return False

    def is_ready(self):
        return False


class _ReadyAttributeClient:
    def __init__(self, ready: bool):
        self.is_closed = False
        self.is_ready = ready


class _ReadyEvent:
    def __init__(self, is_set: bool):
        self._is_set = is_set

    def is_set(self):
        return self._is_set


class _ReadyEventClient:
    def __init__(self, flag: bool):
        self.is_closed = False
        self.is_ready = _ReadyEvent(flag)


def test_client_ready_with_method():
    assert is_discord_client_ready(_ReadyMethodClient()) is True


def test_client_not_ready_with_method():
    assert is_discord_client_ready(_NotReadyMethodClient()) is False


@pytest.mark.parametrize("ready", [True, False])
def test_client_ready_with_boolean_attribute(ready):
    assert is_discord_client_ready(_ReadyAttributeClient(ready)) is ready


@pytest.mark.parametrize("flag", [True, False])
def test_client_ready_with_event_like(flag):
    assert is_discord_client_ready(_ReadyEventClient(flag)) is flag

