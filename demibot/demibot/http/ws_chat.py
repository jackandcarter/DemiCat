from __future__ import annotations

import asyncio
import base64
import io
import json
import logging
import random
import time
from dataclasses import dataclass, field
from typing import Dict, List, Set

import discord
from fastapi import HTTPException, WebSocket, WebSocketDisconnect
from sqlalchemy import select

from ..db.models import GuildChannel
from ..db.session import get_session
from .chat_events import emit_event
from .deps import RequestContext, api_key_auth
from .discord_client import discord_client
from .discord_helpers import serialize_message

logger = logging.getLogger(__name__)

# Delay window for batching fan-out.
BATCH_MIN = 0.04
BATCH_MAX = 0.08

PING_INTERVAL = 30.0
PING_TIMEOUT = 60.0


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
        self._send_count = 0
        self._resync_count = 0
        self._connect_count = 0
        self._disconnect_count = 0
        self._sub_count = 0
        self._backfill_total = 0
        self._backfill_batches = 0
        self._ping_task: asyncio.Task | None = None

    async def connect(self, websocket: WebSocket, ctx: RequestContext) -> None:
        await websocket.accept(
            headers=[(b"sec-websocket-extensions", b"permessage-deflate")]
        )
        self.connections[websocket] = ChatConnection(ctx)
        self._connect_count += 1
        logger.info(
            "chat.ws connect path=%s count=%s",
            websocket.scope.get("path", ""),
            self._connect_count,
        )
        if self._ping_task is None:
            try:
                loop = asyncio.get_running_loop()
            except RuntimeError:
                loop = None
            if loop is not None:
                self._ping_task = loop.create_task(self._ping_loop())

    def disconnect(self, websocket: WebSocket) -> None:
        self.connections.pop(websocket, None)
        self._disconnect_count += 1
        logger.info("chat.ws disconnect count=%s", self._disconnect_count)
        if not self.connections and self._ping_task is not None:
            self._ping_task.cancel()
            self._ping_task = None

    async def _ping_loop(self) -> None:
        try:
            while True:
                await asyncio.sleep(PING_INTERVAL)
                dead: list[WebSocket] = []
                for ws in list(self.connections.keys()):
                    try:
                        await asyncio.wait_for(ws.ping(), PING_TIMEOUT)
                    except Exception:
                        dead.append(ws)
                for ws in dead:
                    self.disconnect(ws)
        except asyncio.CancelledError:
            pass

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
            self._sub_count += 1
            logger.info(
                "chat.ws subscribe channel=%s count=%s",
                channel_id,
                self._sub_count,
            )
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
        batch_size = len(queue)
        self._backfill_total += batch_size
        self._backfill_batches += 1
        avg_backfill = self._backfill_total / self._backfill_batches
        logger.info(
            "chat.ws broadcast channel=%s size=%s avg_backfill=%.1f",
            channel,
            batch_size,
            avg_backfill,
        )
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
        self._resync_count += 1
        logger.info(
            "chat.ws resync channel=%s cursor=%s count=%s",
            channel,
            cursor,
            self._resync_count,
        )
        await websocket.send_text(
            json.dumps({"op": "resync", "channel": channel, "cursor": cursor})
        )

    async def resync(self, websocket: WebSocket, data: dict) -> None:
        channel = str(data.get("channel"))
        await self._send_resync(websocket, channel)

    async def handle(self, websocket: WebSocket, message: dict) -> None:
        op = message.get("op")
        if op == "sub":
            await self.sub(websocket, message)
        elif op == "ack":
            self.ack(websocket, message)
        elif op == "send":
            await self._handle_send(websocket, message)
        elif op == "resync":
            await self.resync(websocket, message)

    async def _handle_send(self, websocket: WebSocket, data: dict) -> None:
        info = self.connections.get(websocket)
        if info is None:
            return
        channel_id = int(data.get("ch") or 0)
        payload = data.get("d") or data.get("payload") or {}
        content = payload.get("content", "")
        attachments = payload.get("attachments") or []
        avatar_url = payload.get("avatar_url")
        async with get_session() as db:
            webhook_url = await db.scalar(
                select(GuildChannel.webhook_url).where(
                    GuildChannel.guild_id == info.ctx.guild.id,
                    GuildChannel.channel_id == channel_id,
                )
            )
        if not webhook_url:
            logger.warning("chat.ws missing webhook channel=%s", channel_id)
            return
        files = []
        for a in attachments:
            b64 = a.get("data") or a.get("content")
            if not b64:
                continue
            try:
                file_bytes = base64.b64decode(b64)
            except Exception:
                continue
            files.append(
                discord.File(io.BytesIO(file_bytes), filename=a.get("filename", "file"))
            )
        username = (
            f"{info.ctx.user.character_name} (DemiCat)"
            if info.ctx.user.character_name
            else "DemiCat"
        )
        start = time.perf_counter()
        try:
            webhook = discord.Webhook.from_url(webhook_url, client=discord_client)
            sent = await webhook.send(
                content,
                username=username,
                avatar_url=avatar_url,
                files=files or None,
                wait=True,
            )
        except Exception:
            logger.exception("chat.ws webhook send failed channel=%s", channel_id)
            return
        latency_ms = (time.perf_counter() - start) * 1000
        self._send_count += 1
        logger.info(
            "chat.ws send channel=%s latency_ms=%.1f count=%s",
            channel_id,
            latency_ms,
            self._send_count,
        )
        dto, _ = serialize_message(sent)
        await emit_event({
            "channel": str(channel_id),
            "op": "mc",
            "d": dto.model_dump(by_alias=True, exclude_none=True),
        })


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
