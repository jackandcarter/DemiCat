from __future__ import annotations

from typing import Any, Dict


async def emit_event(event: Dict[str, Any]) -> None:
    """Emit a chat event to websocket subscribers.

    The event must include a ``channel`` key identifying the target channel. The
    remaining keys form the payload delivered to subscribers. A per-channel
    cursor will be automatically attached by the websocket manager.
    """
    channel = str(event.get("channel", ""))
    if not channel:
        return
    payload = {k: v for k, v in event.items() if k != "channel"}
    # Import lazily to avoid circular imports with :mod:`ws_chat`.
    from .ws_chat import manager

    await manager.send(channel, payload)
