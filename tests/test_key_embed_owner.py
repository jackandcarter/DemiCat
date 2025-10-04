import asyncio
from types import SimpleNamespace

from demibot.discordbot.cogs import admin as admin_module


class DummyResponse:
    def __init__(self) -> None:
        self.kwargs = None

    async def send_message(self, **kwargs) -> None:
        self.kwargs = kwargs


class DummyInteraction:
    def __init__(self, user_id: int) -> None:
        self.guild = SimpleNamespace(id=1, owner_id=999, name="Test Guild")
        perms = SimpleNamespace(administrator=False)
        self.user = SimpleNamespace(id=user_id, roles=[], guild_permissions=perms)
        self.response = DummyResponse()
        self.client = SimpleNamespace(cfg=None)


def test_any_user_can_post_embed():
    inter = DummyInteraction(user_id=1)
    asyncio.run(admin_module.key_embed.callback(inter))

    assert inter.response.kwargs is not None
    assert inter.response.kwargs.get("content") is None
    assert inter.response.kwargs.get("embed") is not None
