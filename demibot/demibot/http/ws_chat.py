from __future__ import annotations

import asyncio
import json
import random
from dataclasses import dataclass, field
from typing import Dict, List, Set

from fastapi import HTTPException, WebSocket, WebSocketDisconnect

from ..db.session import get_session
from .deps import RequestContext, api_key_auth

# Delay window for batching fan-out.
BATCH_MIN = 0.04
BATCH_MAX = 0.08


@dataclass
class ChatConnection:
    ctx: RequestContext
    channels: Set[str] = field(default_factory=set)
    cursors: Dict[str, int] = field(default_factory=dict)


class ChatConnectionManager:
    def __init__(self) -> None:
        self.connections: Dict[WebSocket, ChatConnection] = {}
        self._channel_queues: Dict[str, List[dict]] = {}
        self._channel_tasks: Dict[str, asyncio.Task] = {}
        self._channel_cursors: Dict[str, int] = {}

    async def connect(self, websocket: WebSocket, ctx: RequestContext) -> None:
        await websocket.accept(
            headers=[(b"sec-websocket-extensions", b"permessage-deflate")]
        )
        self.connections[websocket] = ChatConnection(ctx)

    def disconnect(self, websocket: WebSocket) -> None:
        self.connections.pop(websocket, None)

    async def sub(self, websocket: WebSocket, data: dict) -> None:
        info = self.connections.get(websocket)
        if info is None:
            return
        channels = data.get("channels", [])
        for ch in channels:
            channel_id: str
            is_officer = False
            if isinstance(ch, dict):
                channel_id = str(ch.get("id"))
                is_officer = bool(ch.get("officer"))
            else:
                channel_id = str(ch)
            if is_officer and "officer" not in info.ctx.roles:
                # Skip officer-only channels for non-officers.
                continue
            info.channels.add(channel_id)
            await self._send_resync(websocket, channel_id)

    def ack(self, websocket: WebSocket, data: dict) -> None:
        info = self.connections.get(websocket)
        if info is None:
            return
        channel = str(data.get("channel"))
        cursor = int(data.get("cursor", 0))
        info.cursors[channel] = cursor

    async def send(self, channel: str, payload: dict) -> None:
        cursor = self._channel_cursors.get(channel, 0) + 1
        self._channel_cursors[channel] = cursor
        payload = {"cursor": cursor, **payload}
        queue = self._channel_queues.setdefault(channel, [])
        queue.append(payload)
        if channel not in self._channel_tasks:
            self._channel_tasks[channel] = asyncio.create_task(
                self._flush_channel(channel)
            )

    async def _flush_channel(self, channel: str) -> None:
        await asyncio.sleep(random.uniform(BATCH_MIN, BATCH_MAX))
        queue = self._channel_queues.pop(channel, [])
        self._channel_tasks.pop(channel, None)
        if not queue:
            return
        message = json.dumps({"op": "batch", "channel": channel, "messages": queue})
        targets = [
            ws
            for ws, info in self.connections.items()
            if channel in info.channels
        ]
        send_coros = [ws.send_text(message) for ws in targets]
        if send_coros:
            await asyncio.gather(*send_coros, return_exceptions=True)

    async def _send_resync(self, websocket: WebSocket, channel: str) -> None:
        cursor = self._channel_cursors.get(channel, 0)
        await websocket.send_text(
            json.dumps({"op": "resync", "channel": channel, "cursor": cursor})
        )

    async def resync(self, websocket: WebSocket, data: dict) -> None:
        channel = str(data.get("channel"))
        await self._send_resync(websocket, channel)

    async def ping(self, websocket: WebSocket) -> None:
        await websocket.send_text(json.dumps({"op": "pong"}))

    async def handle(self, websocket: WebSocket, message: dict) -> None:
        op = message.get("op")
        if op == "sub":
            await self.sub(websocket, message)
        elif op == "ack":
            self.ack(websocket, message)
        elif op == "send":
            channel = str(message.get("channel"))
            payload = message.get("payload", {})
            await self.send(channel, payload)
        elif op == "resync":
            await self.resync(websocket, message)
        elif op == "ping":
            await self.ping(websocket)


manager = ChatConnectionManager()


async def websocket_endpoint_chat(websocket: WebSocket) -> None:
    path = websocket.scope.get("path", "")
    header_token = websocket.headers.get("X-Api-Key")
    query_token = websocket.query_params.get("token")
    if query_token:
        await websocket.close(code=1008, reason="token in url")
        return
    if not header_token:
        await websocket.close(code=1008, reason="missing token")
        return
    token = header_token

    ctx: RequestContext | None = None
    async with get_session() as db:
        try:
            ctx = await api_key_auth(x_api_key=token, x_discord_id=None, db=db)
        except HTTPException:
            await websocket.close(code=1008, reason="auth failed")
            return
        finally:
            await db.close()

    if ctx is None:
        await websocket.close(code=1008, reason="no context")
        return

    await manager.connect(websocket, ctx)
    try:
        while True:
            data = await websocket.receive_json()
            await manager.handle(websocket, data)
    except WebSocketDisconnect:
        manager.disconnect(websocket)
