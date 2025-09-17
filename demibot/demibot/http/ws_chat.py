from __future__ import annotations

import asyncio
import base64
import io
import json
import logging
import random
import time
from collections import deque
from dataclasses import dataclass, field
from typing import Deque, Dict, List, Set
from types import SimpleNamespace

import discord
from fastapi import HTTPException, WebSocket, WebSocketDisconnect
from sqlalchemy import select

from ..db.models import GuildChannel, Guild
from ..db.session import get_session
from .chat_events import emit_event
from .deps import RequestContext, api_key_auth
from .discord_client import discord_client
from .discord_helpers import serialize_message
from .schemas import EmbedDto, EmbedButtonDto
from .validation import validate_embed_payload

logger = logging.getLogger(__name__)

# Delay window for batching fan-out.
BATCH_MIN = 0.04
BATCH_MAX = 0.08

PING_INTERVAL = 30.0
PING_TIMEOUT = 60.0

MAX_ATTACHMENTS = 10
MAX_ATTACHMENT_SIZE = 25 * 1024 * 1024  # 25MB

# Retry configuration for Discord webhook sends.
RETRY_BASE = 1.0  # seconds
MAX_SEND_ATTEMPTS = 5

HISTORY_LIMIT = 200
HISTORY_CHANNEL_CAP = 500
HISTORY_TTL_SECONDS = 10 * 60  # 10 minutes


def _ws_request(ws: WebSocket):
    client_host = getattr(getattr(ws, "client", None), "host", "unknown")
    return SimpleNamespace(
        client=SimpleNamespace(host=client_host),
        method="WS",
        url=SimpleNamespace(path=ws.scope.get("path", "")),
    )


@dataclass
class ChatConnection:
    ctx: RequestContext
    channels: Set[str] = field(default_factory=set)
    cursors: Dict[str, int] = field(default_factory=dict)
    metadata: Dict[str, "ChannelMeta"] = field(default_factory=dict)


@dataclass
class ChannelMeta:
    guild_id: int | None
    discord_guild_id: int | None
    kind: str | None

    def guild_id_value(self) -> str | None:
        if self.discord_guild_id is not None:
            return str(self.discord_guild_id)
        if self.guild_id is not None:
            return str(self.guild_id)
        return None


@dataclass
class PendingWebhookMessage:
    channel_id: int
    webhook_url: str
    content: str
    username: str
    avatar_url: str | None
    attachments: List[tuple[str, bytes]]
    payload: dict
    attempts: int = 0


class ChatConnectionManager:
    def __init__(self) -> None:
        self.connections: Dict[WebSocket, ChatConnection] = {}
        self._channel_queues: Dict[str, List[dict]] = {}
        self._channel_tasks: Dict[str, asyncio.Task] = {}
        self._channel_cursors: Dict[str, int] = {}
        self._channel_meta: Dict[str, ChannelMeta] = {}
        self._channel_history: Dict[str, Deque[dict]] = {}
        self._channel_last_touch: Dict[str, float] = {}
        self._channel_subscribers: Dict[str, int] = {}
        self._webhook_queues: Dict[int, List[PendingWebhookMessage]] = {}
        self._webhook_tasks: Dict[int, asyncio.Task] = {}
        self._send_count = 0
        self._resync_count = 0
        self._connect_count = 0
        self._disconnect_count = 0
        self._sub_count = 0
        self._backfill_total = 0
        self._backfill_batches = 0
        self._ping_task: asyncio.Task | None = None
        self._webhook_supervisor: asyncio.Task | None = None

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
        info = self.connections.pop(websocket, None)
        if info is not None:
            self._release_connection_channels(info)
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
                self._purge_idle_channels()
        except asyncio.CancelledError:
            pass

    def _release_connection_channels(self, info: ChatConnection) -> None:
        for channel in list(info.channels):
            self._decrement_channel_subscriber(channel)
        info.channels.clear()
        info.cursors.clear()
        info.metadata.clear()

    def _increment_channel_subscriber(self, channel: str) -> None:
        self._channel_subscribers[channel] = (
            self._channel_subscribers.get(channel, 0) + 1
        )

    def _decrement_channel_subscriber(self, channel: str) -> None:
        count = self._channel_subscribers.get(channel)
        if not count:
            return
        if count <= 1:
            self._channel_subscribers.pop(channel, None)
            self._cleanup_channel(channel)
        else:
            self._channel_subscribers[channel] = count - 1

    def _cleanup_channel(self, channel: str) -> None:
        self._drop_channel_history(channel)
        queue = self._channel_queues.pop(channel, None)
        if queue:
            queue.clear()
        task = self._channel_tasks.pop(channel, None)
        if task is not None and not task.done():
            task.cancel()
        self._channel_meta.pop(channel, None)
        self._channel_cursors.pop(channel, None)
        self._channel_last_touch.pop(channel, None)

    def _drop_channel_history(self, channel: str) -> None:
        self._channel_history.pop(channel, None)

    def _enforce_history_caps(self) -> None:
        now = time.time()
        expired = [
            channel
            for channel, last in list(self._channel_last_touch.items())
            if now - last > HISTORY_TTL_SECONDS
        ]
        for channel in expired:
            self._drop_channel_history(channel)

        if HISTORY_CHANNEL_CAP <= 0:
            return
        if len(self._channel_history) <= HISTORY_CHANNEL_CAP:
            return

        channels_by_touch = sorted(
            self._channel_history.keys(),
            key=lambda ch: self._channel_last_touch.get(ch, 0.0),
        )
        for channel in channels_by_touch:
            if len(self._channel_history) <= HISTORY_CHANNEL_CAP:
                break
            if self._channel_subscribers.get(channel):
                continue
            self._drop_channel_history(channel)

        if len(self._channel_history) <= HISTORY_CHANNEL_CAP:
            return

        for channel in channels_by_touch:
            if len(self._channel_history) <= HISTORY_CHANNEL_CAP:
                break
            self._drop_channel_history(channel)

    def _purge_idle_channels(self) -> None:
        now = time.time()
        for channel, last in list(self._channel_last_touch.items()):
            if now - last <= HISTORY_TTL_SECONDS:
                continue
            if self._channel_subscribers.get(channel):
                continue
            self._cleanup_channel(channel)

    async def sub(self, websocket: WebSocket, data: dict) -> None:
        info = self.connections.get(websocket)
        if info is None:
            return
        channels = data.get("channels", [])
        old_channels = set(info.channels)
        new_channels: Set[str] = set()
        expected_meta: Dict[str, tuple[str | None, str | None]] = {}
        since_map: Dict[str, int | None] = {}
        for ch in channels:
            channel_id: str
            is_officer = False
            expected_guild: str | None = None
            expected_kind: str | None = None
            since_value: str | int | None = None
            since_parsed: int | None = None
            if isinstance(ch, dict):
                raw_id = ch.get("id")
                if raw_id is None:
                    continue
                channel_id = str(raw_id)
                is_officer = bool(ch.get("officer"))
                guild_val = ch.get("guildId")
                if guild_val is not None:
                    expected_guild = str(guild_val).strip()
                    if not expected_guild:
                        expected_guild = None
                    elif expected_guild.lower() == "default":
                        expected_guild = None
                kind_val = ch.get("kind")
                if kind_val is not None:
                    expected_kind = str(kind_val).strip()
                    if expected_kind:
                        expected_kind = expected_kind.upper()
                    else:
                        expected_kind = None
                if "since" in ch:
                    since_value = ch.get("since")
            else:
                if ch is None:
                    continue
                channel_id = str(ch)
            logger.info(
                "chat.ws sub request guild=%s kind=%s channel=%s since=%s",
                expected_guild,
                expected_kind,
                channel_id,
                since_value,
            )
            if is_officer and "officer" not in info.ctx.roles:
                # Skip officer-only channels for non-officers.
                continue
            new_channels.add(channel_id)
            expected_meta[channel_id] = (expected_guild, expected_kind)
            if since_value is not None:
                try:
                    since_parsed = int(str(since_value).strip())
                except (TypeError, ValueError):
                    since_parsed = None
            since_map[channel_id] = since_parsed
        removed = old_channels - new_channels
        for ch in removed:
            info.cursors.pop(ch, None)
            info.metadata.pop(ch, None)
            self._decrement_channel_subscriber(ch)
        added = new_channels - old_channels
        retained = old_channels - removed
        valid_added: list[tuple[str, ChannelMeta]] = []
        if added:
            meta_map = await self._fetch_channel_meta_bulk(added)
            for channel_id in added:
                meta = meta_map.get(channel_id)
                if meta is None:
                    logger.info(
                        "chat.ws subscribe drop channel=%s reason=missing_meta",
                        channel_id,
                    )
                    continue
                expected_guild, expected_kind = expected_meta.get(channel_id, (None, None))
                actual_guild = meta.guild_id_value()
                if expected_guild and actual_guild and expected_guild != actual_guild:
                    logger.info(
                        "chat.ws subscribe drop channel=%s reason=guild_mismatch expected=%s actual=%s",
                        channel_id,
                        expected_guild,
                        actual_guild,
                    )
                    continue
                if expected_kind and meta.kind and expected_kind != meta.kind:
                    logger.info(
                        "chat.ws subscribe drop channel=%s reason=kind_mismatch expected=%s actual=%s",
                        channel_id,
                        expected_kind,
                        meta.kind,
                    )
                    continue
                valid_added.append((channel_id, meta))
        info.channels = retained | {channel_id for channel_id, _ in valid_added}
        for channel_id, _ in valid_added:
            self._increment_channel_subscriber(channel_id)
        if retained:
            missing_meta = {ch for ch in retained if ch not in info.metadata}
            if missing_meta:
                retained_meta = await self._fetch_channel_meta_bulk(missing_meta)
                for ch, meta in retained_meta.items():
                    if meta is not None:
                        info.metadata[ch] = meta
        for channel_id, meta in valid_added:
            info.metadata[channel_id] = meta
        for channel_id, _ in valid_added:
            self._sub_count += 1
            logger.info(
                "chat.ws subscribe channel=%s count=%s",
                channel_id,
                self._sub_count,
            )
        for channel_id, meta in valid_added:
            history = list(self._channel_history.get(channel_id, ()))
            since = since_map.get(channel_id)
            if since is not None:
                filtered_history: list[dict] = []
                for msg in history:
                    cursor = msg.get("cursor")
                    if cursor is not None and cursor > since:
                        filtered_history.append(msg)
                history = filtered_history
            if history:
                payload = {
                    "op": "batch",
                    "guildId": meta.guild_id_value(),
                    "channel": channel_id,
                    "kind": meta.kind,
                    "messages": history,
                }
                await websocket.send_text(json.dumps(payload))
        for channel_id, meta in valid_added:
            await self._send_subscription_ack(websocket, channel_id, meta)
            await self._send_resync(websocket, channel_id, meta)

    def ack(self, websocket: WebSocket, data: dict) -> None:
        info = self.connections.get(websocket)
        if info is None:
            return
        channel_val = data.get("channel", data.get("ch"))
        if channel_val is None:
            return
        channel = str(channel_val)
        if channel not in info.channels:
            return
        cursor_val = data.get("cursor", data.get("cur"))
        try:
            cursor = int(cursor_val)
        except (TypeError, ValueError):
            return
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

    async def _fetch_channel_meta_bulk(
        self, channel_ids: Set[str]
    ) -> Dict[str, ChannelMeta | None]:
        if not channel_ids:
            return {}
        metadata: Dict[str, ChannelMeta | None] = {ch: None for ch in channel_ids}
        id_map: Dict[int, str] = {}
        for ch in channel_ids:
            try:
                channel_int = int(ch)
            except (TypeError, ValueError):
                continue
            id_map[channel_int] = ch
        if not id_map:
            return metadata
        async with get_session() as db:
            result = await db.execute(
                select(
                    GuildChannel.channel_id,
                    GuildChannel.guild_id,
                    GuildChannel.kind,
                    Guild.discord_guild_id,
                )
                .join(Guild, Guild.id == GuildChannel.guild_id)
                .where(GuildChannel.channel_id.in_(list(id_map.keys())))
            )
            for channel_id, guild_id, kind, discord_guild_id in result.all():
                ch = id_map.get(channel_id)
                if ch is None:
                    continue
                kind_value: str | None
                if kind is None:
                    kind_value = None
                else:
                    kind_value = (
                        kind.value if hasattr(kind, "value") else str(kind)
                    )
                    kind_value = kind_value.upper() if kind_value else None
                metadata[ch] = ChannelMeta(
                    guild_id=guild_id,
                    discord_guild_id=discord_guild_id,
                    kind=kind_value,
                )
        for ch, meta in metadata.items():
            if meta is not None:
                self._channel_meta[ch] = meta
        return metadata

    async def _ensure_channel_meta(self, channel: str) -> ChannelMeta | None:
        meta = self._channel_meta.get(channel)
        if meta is not None:
            return meta
        result = await self._fetch_channel_meta_bulk({channel})
        return result.get(channel)

    async def _flush_channel(self, channel: str) -> None:
        await asyncio.sleep(random.uniform(BATCH_MIN, BATCH_MAX))
        queue = self._channel_queues.pop(channel, [])
        self._channel_tasks.pop(channel, None)
        if not queue:
            return
        batch_size = len(queue)
        self._backfill_total += batch_size
        self._backfill_batches += 1
        meta = await self._ensure_channel_meta(channel)
        if meta is None:
            logger.warning("chat.ws missing metadata channel=%s", channel)
            return
        history = self._channel_history.setdefault(channel, deque())
        for message in queue:
            history.append(message)
            while len(history) > HISTORY_LIMIT:
                history.popleft()
        self._channel_last_touch[channel] = time.time()
        self._enforce_history_caps()
        guild_id = meta.guild_id_value()
        logger.info(
            "chat.ws batch guild=%s kind=%s channel=%s size=%s",
            guild_id,
            meta.kind,
            channel,
            batch_size,
        )
        payload = {
            "op": "batch",
            "guildId": guild_id,
            "channel": channel,
            "kind": meta.kind,
            "messages": queue,
        }
        message = json.dumps(payload)
        targets: list[WebSocket] = []
        for ws, info in self.connections.items():
            if channel not in info.channels:
                continue
            expected = info.metadata.get(channel)
            if expected is not None:
                if meta.kind and expected.kind and expected.kind != meta.kind:
                    logger.info(
                        "chat.ws drop batch reason=kind_mismatch channel=%s got=%s expect=%s",
                        channel,
                        meta.kind,
                        expected.kind,
                    )
                    continue
                expected_guild = expected.guild_id_value()
                if expected_guild and guild_id and expected_guild != guild_id:
                    logger.info(
                        "chat.ws drop batch reason=guild_mismatch channel=%s got=%s expect=%s",
                        channel,
                        guild_id,
                        expected_guild,
                    )
                    continue
            targets.append(ws)
        send_coros = [ws.send_text(message) for ws in targets]
        if send_coros:
            await asyncio.gather(*send_coros, return_exceptions=True)

    async def _queue_webhook(self, msg: PendingWebhookMessage) -> None:
        queue = self._webhook_queues.setdefault(msg.channel_id, [])
        queue.append(msg)
        if msg.channel_id not in self._webhook_tasks:
            self._webhook_tasks[msg.channel_id] = asyncio.create_task(
                self._run_webhook_queue(msg.channel_id)
            )
            self._ensure_webhook_supervisor()

    async def _run_webhook_queue(self, channel_id: int) -> None:
        try:
            await self._process_webhook_queue(channel_id)
        except asyncio.CancelledError:
            raise
        except Exception:
            logger.exception(
                "chat.ws webhook task crashed channel=%s", channel_id
            )
            queue = self._webhook_queues.get(channel_id)
            self._webhook_tasks.pop(channel_id, None)
            if queue:
                self._webhook_tasks[channel_id] = asyncio.create_task(
                    self._run_webhook_queue(channel_id)
                )

    async def _process_webhook_queue(self, channel_id: int) -> None:
        queue = self._webhook_queues.get(channel_id)
        while queue:
            try:
                msg = queue[0]
                success, retry_after = await self._send_webhook(msg)
                if success:
                    queue.pop(0)
                    continue
                msg.attempts += 1
                if msg.attempts >= MAX_SEND_ATTEMPTS:
                    logger.error(
                        "chat.ws webhook give up channel=%s attempts=%s",
                        channel_id,
                        msg.attempts,
                    )
                    queue.pop(0)
                    await emit_event(
                        {
                            "channel": str(channel_id),
                            "op": "mf",
                            "d": msg.payload,
                        }
                    )
                    continue
                delay = max(RETRY_BASE * (2 ** (msg.attempts - 1)), retry_after)
                await asyncio.sleep(delay)
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception(
                    "chat.ws webhook queue error channel=%s", channel_id
                )
                await asyncio.sleep(1.0)
        self._webhook_tasks.pop(channel_id, None)
        self._webhook_queues.pop(channel_id, None)

    def _ensure_webhook_supervisor(self) -> None:
        if self._webhook_supervisor is None or self._webhook_supervisor.done():
            try:
                loop = asyncio.get_running_loop()
            except RuntimeError:
                return
            self._webhook_supervisor = loop.create_task(
                self._supervise_webhook_tasks()
            )

    async def _supervise_webhook_tasks(self) -> None:
        try:
            while self._webhook_tasks:
                await asyncio.sleep(5)
                for cid, task in list(self._webhook_tasks.items()):
                    if task.done():
                        exc = task.exception()
                        if exc:
                            logger.warning(
                                "chat.ws webhook task failed channel=%s", cid
                            )
                        if self._webhook_queues.get(cid):
                            self._webhook_tasks[cid] = asyncio.create_task(
                                self._run_webhook_queue(cid)
                            )
                        else:
                            self._webhook_tasks.pop(cid, None)
        except asyncio.CancelledError:
            pass
        finally:
            self._webhook_supervisor = None

    async def _send_webhook(self, msg: PendingWebhookMessage) -> tuple[bool, float]:
        webhook = discord.Webhook.from_url(msg.webhook_url, client=discord_client)
        files = [
            discord.File(io.BytesIO(data), filename=name)
            for name, data in msg.attachments
        ]
        start = time.perf_counter()
        channel_str = str(msg.channel_id)
        meta = await self._ensure_channel_meta(channel_str)
        guild_id = meta.guild_id_value() if meta is not None else None
        kind = meta.kind if meta is not None else None
        total_bytes = len(msg.content.encode("utf-8"))
        total_bytes += sum(len(data) for _, data in msg.attachments)
        logger.info(
            "chat.http send guild=%s kind=%s channel=%s bytes=%s",
            guild_id,
            kind,
            msg.channel_id,
            total_bytes,
        )
        try:
            sent = await webhook.send(
                msg.content,
                username=msg.username,
                avatar_url=msg.avatar_url,
                files=files or None,
                wait=True,
            )
        except discord.HTTPException as e:
            headers = getattr(getattr(e, "response", None), "headers", {}) or {}
            retry_after = headers.get("Retry-After") or headers.get(
                "X-RateLimit-Reset-After"
            )
            retry_after_s = float(retry_after) if retry_after is not None else 0.0
            logger.warning(
                "chat.ws webhook send error channel=%s status=%s attempt=%s",
                msg.channel_id,
                getattr(e, "status", None),
                msg.attempts + 1,
            )
            return False, retry_after_s
        except Exception:
            logger.exception(
                "chat.ws webhook send failed channel=%s attempt=%s",
                msg.channel_id,
                msg.attempts + 1,
            )
            return False, 0.0
        latency_ms = (time.perf_counter() - start) * 1000
        self._send_count += 1
        logger.info(
            "chat.ws send channel=%s latency_ms=%.1f count=%s",
            msg.channel_id,
            latency_ms,
            self._send_count,
        )
        dto, _ = serialize_message(sent)
        await emit_event(
            {
                "channel": str(msg.channel_id),
                "op": "mc",
                "d": dto.model_dump(by_alias=True, exclude_none=True),
            }
        )
        return True, 0.0

    async def _send_subscription_ack(
        self, websocket: WebSocket, channel: str, meta: ChannelMeta
    ) -> None:
        guild_id = meta.guild_id_value()
        payload = {
            "op": "ack",
            "channel": channel,
            "guildId": guild_id,
            "kind": meta.kind,
        }
        logger.info(
            "chat.ws sub ack guild=%s kind=%s channel=%s",
            guild_id,
            meta.kind,
            channel,
        )
        await websocket.send_text(json.dumps(payload))

    async def _send_resync(
        self,
        websocket: WebSocket,
        channel: str,
        meta: ChannelMeta | None = None,
    ) -> None:
        cursor = self._channel_cursors.get(channel, 0)
        if meta is None:
            meta = await self._ensure_channel_meta(channel)
        if meta is None:
            logger.info("chat.ws resync skipped channel=%s reason=missing_meta", channel)
            return
        guild_id = meta.guild_id_value()
        self._resync_count += 1
        logger.info(
            "chat.ws resync channel=%s cursor=%s count=%s",
            channel,
            cursor,
            self._resync_count,
        )
        await websocket.send_text(
            json.dumps(
                {
                    "op": "resync",
                    "guildId": guild_id,
                    "kind": meta.kind,
                    "channel": channel,
                    "cursor": cursor,
                }
            )
        )

    async def resync(self, websocket: WebSocket, data: dict) -> None:
        info = self.connections.get(websocket)
        if info is None:
            return
        channel_val = data.get("channel")
        if channel_val is None:
            return
        channel = str(channel_val)
        if channel not in info.channels:
            return
        meta = info.metadata.get(channel)
        if meta is None:
            meta = await self._ensure_channel_meta(channel)
            if meta is None:
                return
            info.metadata[channel] = meta
        await self._send_resync(websocket, channel, meta)

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
        embeds_payload = payload.get("embeds") or []
        buttons_payload = payload.get("buttons") or []
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
        if len(attachments) > MAX_ATTACHMENTS:
            logger.warning("chat.ws too many attachments channel=%s", channel_id)
            return
        file_data: List[tuple[str, bytes]] = []
        for a in attachments:
            b64 = a.get("data") or a.get("content")
            if not b64:
                continue
            try:
                file_bytes = base64.b64decode(b64)
            except Exception:
                continue
            if len(file_bytes) > MAX_ATTACHMENT_SIZE:
                logger.warning("chat.ws attachment too large channel=%s", channel_id)
                return
            file_data.append((a.get("filename", "file"), file_bytes))

        try:
            for e in embeds_payload:
                dto = EmbedDto(**e)
                btns = [EmbedButtonDto(**b) for b in buttons_payload]
                validate_embed_payload(dto, btns)
        except Exception:
            logger.warning("chat.ws invalid embed payload channel=%s", channel_id)
            return
        username = (
            f"{info.ctx.user.character_name} (DemiCat)"
            if info.ctx.user.character_name
            else "DemiCat"
        )
        msg = PendingWebhookMessage(
            channel_id=channel_id,
            webhook_url=webhook_url,
            content=content,
            username=username,
            avatar_url=avatar_url,
            attachments=file_data,
            payload=payload,
        )
        await self._queue_webhook(msg)


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
            ctx = await api_key_auth(
                _ws_request(websocket),
                x_api_key=token,
                x_discord_id=None,
                db=db,
            )
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
