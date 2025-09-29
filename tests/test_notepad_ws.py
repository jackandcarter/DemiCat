import json
import types

import pytest

from demibot.http.ws import ConnectionManager


class StubWebSocket:
    def __init__(self, path: str):
        self.scope = {"path": path}
        self.sent: list[str] = []

    async def accept(self) -> None:
        pass

    async def send_text(self, message: str) -> None:
        self.sent.append(message)


class StubContext:
    def __init__(self, guild_id: int, roles: list[str]):
        self.guild = types.SimpleNamespace(id=guild_id)
        self.roles = roles


@pytest.fixture
def anyio_backend():
    return "asyncio"


@pytest.mark.anyio
async def test_notepad_broadcasts_to_all_roles():
    manager = ConnectionManager()
    ws_officer = StubWebSocket("/ws/notepad")
    ws_member = StubWebSocket("/ws/notepad")

    await manager.connect(ws_officer, StubContext(1, ["officer"]))
    await manager.connect(ws_member, StubContext(1, []))

    payload = {"topic": "notepad.section.created", "payload": {}}
    message = json.dumps(payload)
    await manager.broadcast_text(message, 1, path="/ws/notepad")

    assert ws_officer.sent == [message]
    assert ws_member.sent == [message]

    manager.disconnect(ws_officer)
    manager.disconnect(ws_member)
