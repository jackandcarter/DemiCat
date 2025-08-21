import asyncio
from types import SimpleNamespace

import asyncio
from types import SimpleNamespace

from demibot.discordbot.cogs import admin as admin_module


class DummyResponse:
    def __init__(self) -> None:
        self.kwargs = None

    async def send_message(self, **kwargs) -> None:
        self.kwargs = kwargs


class DummyInteraction:
    def __init__(self, owner_id: int, user_id: int) -> None:
        self.guild = SimpleNamespace(id=1, owner_id=owner_id, name="Test Guild")
        perms = SimpleNamespace(administrator=False)
        self.user = SimpleNamespace(id=user_id, roles=[], guild_permissions=perms)
        self.response = DummyResponse()
        self.client = SimpleNamespace(cfg=None)


def test_owner_can_post_embed(monkeypatch):
    async def stub_authorized_role_ids(_guild_id: int) -> set[int]:
        return set()

    monkeypatch.setattr(
        admin_module, "_authorized_role_ids", stub_authorized_role_ids
    )

    inter = DummyInteraction(owner_id=1, user_id=1)
    asyncio.run(admin_module.key_embed.callback(inter))

    assert inter.response.kwargs is not None
    assert inter.response.kwargs.get("content") is None
    assert inter.response.kwargs.get("embed") is not None
