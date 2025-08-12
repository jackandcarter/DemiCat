"""Simple WebSocket broadcasting for DemiBot.

This mirrors the minimal functionality of
``discord-demibot/src/http/ws.js`` by maintaining two hubs for
messages and embeds and broadcasting cached payloads to connected
clients.  The implementation is intentionally lightweight and tailored
for FastAPI.
"""

from __future__ import annotations

from typing import Dict, Set

from fastapi import APIRouter, FastAPI, WebSocket, WebSocketDisconnect

message_clients: Set[WebSocket] = set()
embed_clients: Set[WebSocket] = set()


def start(app: FastAPI, discord) -> None:
    """Register WebSocket routes on the given FastAPI application."""
    router = APIRouter()

    @router.websocket("/ws/messages")
    async def ws_messages(ws: WebSocket) -> None:  # pragma: no cover - network
        await ws.accept()
        message_clients.add(ws)
        try:
            for arr in discord.message_cache.values():
                for msg in arr:
                    await ws.send_json(msg)
            while True:
                await ws.receive_text()
        except WebSocketDisconnect:
            message_clients.discard(ws)

    @router.websocket("/ws/embeds")
    async def ws_embeds(ws: WebSocket) -> None:  # pragma: no cover - network
        await ws.accept()
        embed_clients.add(ws)
        try:
            for embed in discord.embed_cache:
                await ws.send_json(embed)
            while True:
                await ws.receive_text()
        except WebSocketDisconnect:
            embed_clients.discard(ws)

    app.include_router(router)


async def _broadcast(clients: Set[WebSocket], payload: Dict) -> None:
    dead: Set[WebSocket] = set()
    for ws in list(clients):
        try:
            await ws.send_json(payload)
        except Exception:
            dead.add(ws)
    clients.difference_update(dead)


async def broadcast_message(msg: Dict) -> None:
    """Send a message payload to all listeners."""
    await _broadcast(message_clients, msg)


async def broadcast_embed(embed: Dict) -> None:
    """Send an embed payload to all listeners."""
    await _broadcast(embed_clients, embed)
