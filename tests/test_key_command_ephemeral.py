import asyncio
import sys
from pathlib import Path
from types import SimpleNamespace
import types


root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

discordbot_pkg = types.ModuleType("demibot.discordbot")
discordbot_pkg.__path__ = [str(root / "demibot/discordbot")]
sys.modules.setdefault("demibot.discordbot", discordbot_pkg)


from demibot.discordbot.cogs import keygen as keygen_module
from contextlib import asynccontextmanager


class DummyResponse:
    def __init__(self) -> None:
        self.kwargs: dict | None = None

    async def send_message(self, *args, **kwargs) -> None:
        self.kwargs = kwargs


class DummyUser:
    def __init__(self) -> None:
        self.id = 1
        self.global_name = "Member"
        self.discriminator = "0001"
        self.roles = []
        self.dm_called = False

    async def send(self, *args, **kwargs) -> None:
        self.dm_called = True


class DummyDB:
    async def execute(self, *args, **kwargs):
        class DummyScalar:
            def first(self):
                return None

            def all(self):
                return []

            def __iter__(self):
                return iter([])

        class DummyResult:
            def scalars(self):
                return DummyScalar()

        return DummyResult()

    def add(self, *args, **kwargs):
        pass

    async def flush(self):
        pass

    async def commit(self):
        pass


@asynccontextmanager
async def dummy_get_session():
    yield DummyDB()


class DummyInteraction:
    def __init__(self) -> None:
        self.guild = SimpleNamespace(id=1, name="Test Guild")
        self.user = DummyUser()
        self.response = DummyResponse()


async def _run() -> DummyInteraction:
    keygen_module.get_session = dummy_get_session
    inter = DummyInteraction()
    await keygen_module.key_command.callback(inter)
    return inter


def test_key_sent_ephemeral_and_no_dm():
    inter = asyncio.run(_run())
    assert inter.user.dm_called is False
    assert inter.response.kwargs is not None
    assert inter.response.kwargs.get("embed") is not None
    assert inter.response.kwargs.get("ephemeral") is True
