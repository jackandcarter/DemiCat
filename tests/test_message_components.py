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
    assert dto.components[0].customId == "test"
    assert dto.components[0].style == ButtonStyle.primary
