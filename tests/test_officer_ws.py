import types
import pytest

from demibot.http.ws import ConnectionManager

class StubWebSocket:
    def __init__(self, path: str):
        self.scope = {"path": path}
        self.sent: list[str] = []
    async def accept(self):
        pass
    async def send_text(self, message: str):
        self.sent.append(message)

class StubContext:
    def __init__(self, guild_id: int, roles):
        self.guild = types.SimpleNamespace(id=guild_id)
        self.roles = roles

@pytest.mark.anyio("asyncio")
async def test_officer_broadcast_filtered_by_path_and_role():
    manager = ConnectionManager()
    # Officer connected to officer path
    ws_officer = StubWebSocket("/ws/officer-messages")
    ctx_officer = StubContext(1, ["officer"])
    await manager.connect(ws_officer, ctx_officer)

    # Officer connected to general path
    ws_general = StubWebSocket("/ws/messages")
    ctx_general = StubContext(1, ["officer"])
    await manager.connect(ws_general, ctx_general)

    # Non-officer connected to officer path
    ws_non_officer = StubWebSocket("/ws/officer-messages")
    ctx_non_officer = StubContext(1, [])
    await manager.connect(ws_non_officer, ctx_non_officer)

    # Officer from another guild
    ws_other_guild = StubWebSocket("/ws/officer-messages")
    ctx_other_guild = StubContext(2, ["officer"])
    await manager.connect(ws_other_guild, ctx_other_guild)

    await manager.broadcast_text("hi", 1, officer_only=True)

    assert ws_officer.sent == ["hi"]
    assert ws_general.sent == []
    assert ws_non_officer.sent == []
    assert ws_other_guild.sent == []
