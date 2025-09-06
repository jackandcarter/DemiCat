import types
import asyncio

from demibot.http.ws import ConnectionManager


class StubWebSocket:
    def __init__(self, path: str):
        self.scope = {"path": path}
        self.sent: list[str] = []

    async def accept(self):
        pass

    async def send_text(self, message: str):
        self.sent.append(message)

    async def ping(self):
        return None


class StubContext:
    def __init__(self, guild_id: int, roles):
        self.guild = types.SimpleNamespace(id=guild_id)
        self.roles = roles


def test_officer_filter_on_templates_path():
    async def _run():
        manager = ConnectionManager()
        ws_officer = StubWebSocket("/ws/templates")
        ctx_officer = StubContext(1, ["officer"])
        await manager.connect(ws_officer, ctx_officer)
        ws_member = StubWebSocket("/ws/templates")
        ctx_member = StubContext(1, [])
        await manager.connect(ws_member, ctx_member)

        await manager.broadcast_text("hi", 1, officer_only=True, path="/ws/templates")

        assert ws_officer.sent == ["hi"]
        assert ws_member.sent == []

    asyncio.run(_run())
