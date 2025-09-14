import types
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

# Provide a minimal stub for the ``discord`` module so the helpers can be
# imported without the real dependency.
discord = types.ModuleType("discord")
abc = types.ModuleType("discord.abc")
discord.abc = abc
sys.modules.setdefault("discord", discord)
sys.modules.setdefault("discord.abc", abc)

from demibot.http.discord_helpers import message_to_chat_message
from demibot.http.schemas import ButtonStyle


class DummyAuthor:
    def __init__(self):
        self.id = 1
        self.display_name = "Author"
        self.name = "Author"
        self.display_avatar = None


class DummyChannel:
    def __init__(self):
        self.id = 123


class DummyMessage:
    def __init__(self):
        btn = types.SimpleNamespace(
            type=2,
            custom_id="test",
            label="Test",
            style=1,
            emoji=None,
            url="https://example.com",
        )
        row = types.SimpleNamespace(children=[btn])
        self.components = [row]
        self.attachments = []
        self.mentions = []
        self.embeds = []
        self.reference = None
        self.author = DummyAuthor()
        self.channel = DummyChannel()
        self.id = 42
        self.content = "hello"
        self.created_at = None
        self.edited_at = None


def test_message_components_to_dtos():
    msg = DummyMessage()
    dto = message_to_chat_message(msg)
    assert dto.components is not None
    assert dto.components[0].label == "Test"
    assert dto.components[0].custom_id == "test"
    assert dto.components[0].style == ButtonStyle.primary


class DummyEmbed:
    def __init__(self):
        self.timestamp = None
        self.color = None
        self.title = None
        self.description = None
        self.url = None
        self.fields = []
        self.thumbnail = None
        self.image = None

    def to_dict(self):
        return {}


class MalformedMessage:
    def __init__(self):
        row = types.SimpleNamespace(children=123)  # not iterable
        self.components = [row]
        self.attachments = []
        self.mentions = []
        self.embeds = [DummyEmbed()]
        self.reference = None
        self.author = DummyAuthor()
        self.channel = DummyChannel()
        self.id = 99
        self.content = "oops"
        self.created_at = None
        self.edited_at = None


def test_malformed_message_components_handled():
    msg = MalformedMessage()
    dto = message_to_chat_message(msg)
    assert dto.components is None
    assert dto.embeds is not None
    assert dto.embeds[0].buttons is None
