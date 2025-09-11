import types
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

# Ensure packages are importable
pkg = types.ModuleType("demibot")
pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", pkg)
http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

# Stub discord module with minimal classes
discord = types.ModuleType("discord")
abc = types.ModuleType("discord.abc")

class Snowflake:
    def __init__(self, id):
        self.id = id

class User(Snowflake):
    def __init__(self, id, name):
        super().__init__(id)
        self.display_name = name
        self.name = name
        self.bot = False

class Role(Snowflake):
    def __init__(self, id, name):
        super().__init__(id)
        self.name = name

class GuildChannel(Snowflake):
    def __init__(self, id, name):
        super().__init__(id)
        self.name = name

discord.User = User
discord.Member = User
discord.Role = Role
abc.GuildChannel = GuildChannel
discord.abc = abc

sys.modules.setdefault("discord", discord)
sys.modules.setdefault("discord.abc", abc)

from demibot.http.discord_helpers import message_to_chat_message


class DummyUser(User):
    def __init__(self):
        super().__init__(1, "User")


class DummyRole(Role):
    def __init__(self):
        super().__init__(2, "Role")


class DummyChannel(GuildChannel):
    def __init__(self):
        super().__init__(3, "general")


class DummyMessage:
    def __init__(self):
        self.mentions = [DummyUser()]
        self.role_mentions = [DummyRole()]
        self.channel_mentions = [DummyChannel()]
        self.attachments = []
        self.embeds = []
        self.reference = None
        self.components = []
        self.reactions = []
        self.author = DummyUser()
        self.channel = types.SimpleNamespace(id=123)
        self.id = 42
        self.content = "<@1> <@&2> <#3>"
        self.created_at = None
        self.edited_at = None


def test_message_mentions_roundtrip():
    msg = DummyMessage()
    dto = message_to_chat_message(msg)
    assert dto.mentions is not None
    assert {m.type for m in dto.mentions} == {"user", "role", "channel"}
    mapping = {m.type: m for m in dto.mentions}
    assert mapping["user"].id == "1"
    assert mapping["role"].id == "2"
    assert mapping["channel"].id == "3"
