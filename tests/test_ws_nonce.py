import asyncio

import pytest

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "demibot"
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from demibot.bridge import BRIDGE_MARKER
from demibot.http.ws_chat import ChatConnectionManager


@pytest.mark.asyncio
async def test_should_drop_due_to_nonce_and_cleanup(monkeypatch):
    manager = ChatConnectionManager()

    flush_calls: list[str] = []

    async def fake_flush(self, channel: str) -> None:
        flush_calls.append(channel)

    monkeypatch.setattr(
        ChatConnectionManager,
        "_flush_channel",
        fake_flush,
    )

    payload = {
        "op": "mc",
        "d": {
            "id": "42",
            "content": "hello",
            "embeds": [
                {
                    "footer": {"text": f"DemiCat • Chat • {BRIDGE_MARKER}abc123"},
                }
            ],
        },
    }

    await manager.send("100", payload)
    await asyncio.sleep(0)
    assert "100" in manager._channel_nonce_cache
    assert len(manager._channel_queues.get("100", [])) == 1

    await manager.send("100", payload)
    await asyncio.sleep(0)
    assert len(manager._channel_queues.get("100", [])) == 1

    manager._cleanup_channel("100")
    assert "100" not in manager._channel_nonce_cache
    assert "100" not in manager._channel_nonce_order
    assert "100" not in manager._channel_queues
