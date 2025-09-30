from __future__ import annotations

import asyncio
import base64
import inspect
import json
import logging
import random
import time
from collections import defaultdict, deque
from dataclasses import dataclass, field
from typing import Deque, Dict, List, Mapping, Set
from types import SimpleNamespace

import discord
from fastapi import HTTPException, WebSocket, WebSocketDisconnect
from sqlalchemy import select

from ..db.models import GuildChannel, Guild, ChannelKind, Membership
from ..db.session import get_session
from .chat_events import emit_event
from .deps import RequestContext, api_key_auth
from .discord_client import discord_client
from .discord_helpers import serialize_message
from .discord_allowed_mentions import ALLOWED_MENTIONS
from .routes._messages_common import create_webhook_for_channel, _channel_webhooks
from ..bridge import (
    BridgeUpload,
    build_bridge_message,
    extract_bridge_nonce_from_payload,
)

logger = logging.getLogger(__name__)

# Delay window for batching fan-out.
BATCH_MIN = 0.04
BATCH_MAX = 0.08

PING_INTERVAL = 30.0
PING_TIMEOUT = 60.0
HEARTBEAT_TIMEOUT = 5.0
HEARTBEAT_PAYLOAD = "{\"op\":\"ping\"}"

MAX_ATTACHMENTS = 10
MAX_ATTACHMENT_SIZE = 25 * 1024 * 1024  # 25MB

# Retry configuration for Discord webhook sends.
RETRY_BASE = 1.0  # seconds
MAX_SEND_ATTEMPTS = 5

FATAL_WEBHOOK_STATUSES = {401, 403, 404}

HISTORY_LIMIT = 200
HISTORY_CHANNEL_CAP = 500
HISTORY_TTL_SECONDS = 10 * 60  # 10 minutes

NONCE_CACHE_LIMIT = 256


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


OFFICER_CHAT_KIND = getattr(ChannelKind.OFFICER_CHAT, "value", ChannelKind.OFFICER_CHAT).upper()


@dataclass
class PendingWebhookMessage:
    channel_id: int
    webhook_url: str
    content: str
    username: str
    avatar_url: str | None
    uploads: List[BridgeUpload]
    embeds: List[discord.Embed]
    nonce: str
    payload: dict
    thread_id: int | None = None
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
        self._channel_nonce_cache: Dict[str, Dict[str, str]] = {}
        self._channel_nonce_order: Dict[str, Deque[str]] = {}
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
                        alive = await self._probe_connection(ws)
                    except asyncio.CancelledError:
                        raise
                    except Exception:
                        alive = False
                    if not alive:
                        dead.append(ws)
                for ws in dead:
                    self.disconnect(ws)
                self._purge_idle_channels()
        except asyncio.CancelledError:
            pass

    async def _probe_connection(self, ws: WebSocket) -> bool:
        ping = getattr(ws, "ping", None)
        supports_ping = callable(ping)
        if supports_ping:
            try:
                result = ping()
            except NotImplementedError:
                supports_ping = False
            except asyncio.CancelledError:
                raise
            except Exception:
                return False
            else:
                if result is not None and inspect.isawaitable(result):
                    try:
                        await asyncio.wait_for(result, PING_TIMEOUT)
                    except asyncio.CancelledError:
                        raise
                    except Exception:
                        return False
            if supports_ping:
                return True

        send_text = getattr(ws, "send_text", None)
        if callable(send_text):
            try:
                result = send_text(HEARTBEAT_PAYLOAD)
            except asyncio.CancelledError:
                raise
            except Exception:
                return False
            if result is not None and inspect.isawaitable(result):
                try:
                    await asyncio.wait_for(result, HEARTBEAT_TIMEOUT)
                except asyncio.CancelledError:
                    raise
                except Exception:
                    return False
            return True

        return True

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
        self._channel_nonce_cache.pop(channel, None)
        self._channel_nonce_order.pop(channel, None)

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
        has_officer_role = "officer" in info.ctx.roles

        def is_authorized(meta: ChannelMeta | None) -> bool:
            if meta is None:
                return True
            if meta.kind == OFFICER_CHAT_KIND and not has_officer_role:
                return False
            return True

        channels = data.get("channels", [])
        old_channels = set(info.channels)
        new_channels: Set[str] = set()
        expected_meta: Dict[str, tuple[str | None, str | None]] = {}
        since_map: Dict[str, int | None] = {}
        sync_channels: list[str] = []
        sync_channels_seen: set[str] = set()

        def mark_for_sync(channel_id: str) -> None:
            if channel_id in sync_channels_seen:
                return
            sync_channels.append(channel_id)
            sync_channels_seen.add(channel_id)
        for ch in channels:
            channel_id: str
            expected_guild: str | None = None
            expected_kind: str | None = None
            since_value: str | int | None = None
            since_parsed: int | None = None
            if isinstance(ch, dict):
                raw_id = ch.get("id")
                if raw_id is None:
                    continue
                channel_id = str(raw_id)
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
                if not is_authorized(meta):
                    logger.info(
                        "chat.ws subscribe drop channel=%s reason=officer_required kind=%s",
                        channel_id,
                        meta.kind,
                    )
                    continue
                valid_added.append((channel_id, meta))
                info.metadata[channel_id] = meta
                mark_for_sync(channel_id)
        if retained:
            retained_meta = await self._fetch_channel_meta_bulk(retained)
            for channel_id in list(retained):
                meta = retained_meta.get(channel_id)
                if meta is None:
                    logger.info(
                        "chat.ws drop channel=%s reason=missing_meta",
                        channel_id,
                    )
                    info.cursors.pop(channel_id, None)
                    info.metadata.pop(channel_id, None)
                    self._decrement_channel_subscriber(channel_id)
                    retained.discard(channel_id)
                    continue
                expected_guild, expected_kind = expected_meta.get(channel_id, (None, None))
                actual_guild = meta.guild_id_value()
                if expected_guild and actual_guild and expected_guild != actual_guild:
                    logger.info(
                        "chat.ws drop channel=%s reason=guild_mismatch expected=%s actual=%s",
                        channel_id,
                        expected_guild,
                        actual_guild,
                    )
                    info.cursors.pop(channel_id, None)
                    info.metadata.pop(channel_id, None)
                    self._decrement_channel_subscriber(channel_id)
                    retained.discard(channel_id)
                    continue
                if expected_kind and meta.kind and expected_kind != meta.kind:
                    logger.info(
                        "chat.ws drop channel=%s reason=kind_mismatch expected=%s actual=%s",
                        channel_id,
                        expected_kind,
                        meta.kind,
                    )
                    info.cursors.pop(channel_id, None)
                    info.metadata.pop(channel_id, None)
                    self._decrement_channel_subscriber(channel_id)
                    retained.discard(channel_id)
                    continue
                if not is_authorized(meta):
                    logger.info(
                        "chat.ws drop channel=%s reason=officer_required kind=%s",
                        channel_id,
                        meta.kind,
                    )
                    info.cursors.pop(channel_id, None)
                    info.metadata.pop(channel_id, None)
                    self._decrement_channel_subscriber(channel_id)
                    retained.discard(channel_id)
                    continue
                existing_meta = info.metadata.get(channel_id)
                existing_guild = (
                    existing_meta.guild_id_value() if existing_meta is not None else None
                )
                fresh_guild = meta.guild_id_value()
                meta_changed = (
                    existing_meta is None
                    or existing_meta.kind != meta.kind
                    or existing_guild != fresh_guild
                )
                info.metadata[channel_id] = meta
                if meta_changed:
                    mark_for_sync(channel_id)
        info.channels = retained | {channel_id for channel_id, _ in valid_added}
        for channel_id, _ in valid_added:
            self._increment_channel_subscriber(channel_id)
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
                await websocket.send_text(json.dumps(payload, ensure_ascii=False))
        for channel_id in sync_channels:
            meta = info.metadata.get(channel_id)
            if meta is None:
                continue
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

    def _should_drop_due_to_nonce(self, channel: str, payload: Mapping[str, object]) -> bool:
        data = payload.get("d") if isinstance(payload.get("d"), Mapping) else None
        if data is None:
            data = payload if isinstance(payload, Mapping) else None
        if data is None:
            return False

        nonce: str | None = None
        raw_nonce = data.get("nonce") if isinstance(data, Mapping) else None
        if isinstance(raw_nonce, str) and raw_nonce:
            nonce = raw_nonce
        elif isinstance(data, Mapping):
            nonce = extract_bridge_nonce_from_payload(data)

        if not nonce:
            return False

        message_id: str | None = None
        if isinstance(data, Mapping):
            for key in ("id", "messageId", "discordMessageId"):
                value = data.get(key)
                if value is not None:
                    message_id = str(value)
                    break

        op: str | None = None
        op_val = payload.get("op") if isinstance(payload, Mapping) else None
        if isinstance(op_val, str) and op_val:
            op = op_val
        elif isinstance(data, Mapping):
            nested_op = data.get("op")
            if isinstance(nested_op, str) and nested_op:
                op = nested_op

        key_components: list[str] = [nonce]
        if message_id:
            key_components.append(f"id:{message_id}")
        if op:
            key_components.append(f"op:{op}")
        cache_key = "|".join(key_components)

        cache = self._channel_nonce_cache.setdefault(channel, {})
        if cache_key in cache:
            logger.info(
                "chat.ws drop message channel=%s reason=nonce_duplicate nonce=%s op=%s message_id=%s",
                channel,
                nonce,
                op,
                message_id,
            )
            return True

        cache[cache_key] = message_id or ""
        order = self._channel_nonce_order.setdefault(channel, deque())
        order.append(cache_key)
        while len(order) > NONCE_CACHE_LIMIT:
            oldest = order.popleft()
            cache.pop(oldest, None)

        return False

    async def send(self, channel: str, payload: dict) -> None:
        if self._should_drop_due_to_nonce(channel, payload):
            return
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
            rows_by_channel: Dict[str, List[ChannelMeta]] = defaultdict(list)
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
                rows_by_channel[ch].append(
                    ChannelMeta(
                        guild_id=guild_id,
                        discord_guild_id=discord_guild_id,
                        kind=kind_value,
                    )
                )
        chat_kind_value = ChannelKind.CHAT.value.upper()

        def kind_priority(kind: str | None) -> int:
            if not kind:
                return 0
            if kind == chat_kind_value:
                return 1
            return 2

        for ch, candidates in rows_by_channel.items():
            if not candidates:
                continue
            metadata[ch] = max(candidates, key=lambda meta: kind_priority(meta.kind))

        for ch, meta in metadata.items():
            if meta is None:
                continue
            existing = self._channel_meta.get(ch)
            if existing is not None and kind_priority(existing.kind) > kind_priority(
                meta.kind
            ):
                metadata[ch] = existing
                continue
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
        message = json.dumps(payload, ensure_ascii=False)
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

    async def _finalize_webhook_success(
        self,
        msg: PendingWebhookMessage,
        sent: discord.Message,
        *,
        started_at: float,
    ) -> None:
        latency_ms = (time.perf_counter() - started_at) * 1000
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

    async def _recover_stale_webhook(
        self, msg: PendingWebhookMessage, meta: ChannelMeta | None
    ) -> tuple[bool, float]:
        _channel_webhooks.pop(msg.channel_id, None)
        guild_id = meta.guild_id if meta is not None else None
        discord_guild_id = meta.discord_guild_id if meta is not None else None
        channel_kind: ChannelKind | None = None
        if meta is not None and meta.kind:
            try:
                channel_kind = ChannelKind(meta.kind.lower())
            except ValueError:
                channel_kind = None
        should_commit = False
        created = None
        created_url: str | None = None
        creation_errors: list[str] = []
        async with get_session() as db:
            stmt = select(GuildChannel).where(
                GuildChannel.channel_id == msg.channel_id
            )
            if guild_id is not None:
                stmt = stmt.where(GuildChannel.guild_id == guild_id)
            result = await db.execute(stmt)
            rows = result.scalars().all()
            for row in rows:
                if row.webhook_url and (
                    msg.webhook_url is None or row.webhook_url == msg.webhook_url
                ):
                    row.webhook_url = None
                    should_commit = True
                if guild_id is None:
                    guild_id = row.guild_id
                if channel_kind is None and getattr(row, "kind", None) is not None:
                    channel_kind = row.kind
            if guild_id is None:
                if should_commit:
                    await db.commit()
                else:
                    await db.rollback()
                return False, 0.0
            created, created_url, creation_errors = await create_webhook_for_channel(
                channel=None,
                channel_id=msg.channel_id,
                guild_id=guild_id,
                guild_discord_id=discord_guild_id,
                db=db,
                channel_kind=channel_kind,
                configured_channel_id=msg.channel_id,
            )
            if creation_errors:
                logger.warning(
                    "chat.ws webhook recreate errors channel=%s errors=%s",
                    msg.channel_id,
                    creation_errors,
                )
            if created_url:
                msg.webhook_url = created_url
                should_commit = True
            elif created is not None:
                should_commit = True
            if should_commit:
                await db.commit()
            else:
                await db.rollback()
        if created is None:
            return False, 0.0
        retry_start = time.perf_counter()
        files = [upload.to_discord_file() for upload in msg.uploads] or None
        embeds = list(msg.embeds) if msg.embeds else None
        send_kwargs = {
            "username": msg.username,
            "avatar_url": msg.avatar_url,
            "files": files,
            "embeds": embeds,
            "wait": True,
            "allowed_mentions": ALLOWED_MENTIONS,
        }
        if msg.thread_id is not None:
            send_kwargs["thread"] = discord.Object(id=msg.thread_id)
        try:
            sent = await created.send(msg.content, **send_kwargs)
        except discord.HTTPException as e:
            headers = getattr(getattr(e, "response", None), "headers", {}) or {}
            retry_after = headers.get("Retry-After") or headers.get(
                "X-RateLimit-Reset-After"
            )
            retry_after_s = float(retry_after) if retry_after is not None else 0.0
            logger.warning(
                "chat.ws webhook resend error channel=%s status=%s attempt=%s",
                msg.channel_id,
                getattr(e, "status", None),
                msg.attempts + 1,
            )
            return False, retry_after_s
        except Exception:
            logger.exception(
                "chat.ws webhook resend failed channel=%s attempt=%s",
                msg.channel_id,
                msg.attempts + 1,
            )
            return False, 0.0
        await self._finalize_webhook_success(msg, sent, started_at=retry_start)
        return True, 0.0

    async def _send_webhook(self, msg: PendingWebhookMessage) -> tuple[bool, float]:
        webhook = discord.Webhook.from_url(msg.webhook_url, client=discord_client)
        start = time.perf_counter()
        channel_str = str(msg.channel_id)
        meta = await self._ensure_channel_meta(channel_str)
        guild_id = meta.guild_id_value() if meta is not None else None
        kind = meta.kind if meta is not None else None
        total_bytes = len(msg.content.encode("utf-8"))
        total_bytes += sum(len(upload.data) for upload in msg.uploads)
        logger.info(
            "chat.http send guild=%s kind=%s channel=%s bytes=%s",
            guild_id,
            kind,
            msg.channel_id,
            total_bytes,
        )
        files = [upload.to_discord_file() for upload in msg.uploads] or None
        embeds = list(msg.embeds) if msg.embeds else None
        send_kwargs = {
            "username": msg.username,
            "avatar_url": msg.avatar_url,
            "files": files,
            "embeds": embeds,
            "wait": True,
            "allowed_mentions": ALLOWED_MENTIONS,
        }
        if msg.thread_id is not None:
            send_kwargs["thread"] = discord.Object(id=msg.thread_id)
        try:
            sent = await webhook.send(msg.content, **send_kwargs)
        except discord.HTTPException as e:
            headers = getattr(getattr(e, "response", None), "headers", {}) or {}
            retry_after = headers.get("Retry-After") or headers.get(
                "X-RateLimit-Reset-After"
            )
            retry_after_s = float(retry_after) if retry_after is not None else 0.0
            status = getattr(e, "status", None)
            if status in FATAL_WEBHOOK_STATUSES:
                logger.warning(
                    "chat.ws webhook stale channel=%s status=%s attempt=%s",
                    msg.channel_id,
                    status,
                    msg.attempts + 1,
                )
                return await self._recover_stale_webhook(msg, meta)
            logger.warning(
                "chat.ws webhook send error channel=%s status=%s attempt=%s",
                msg.channel_id,
                status,
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
        await self._finalize_webhook_success(msg, sent, started_at=start)
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
        await websocket.send_text(json.dumps(payload, ensure_ascii=False))

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
                },
                ensure_ascii=False,
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
        def _parse_channel_id(raw: object) -> int | None:
            if raw is None:
                return None
            if isinstance(raw, str):
                raw = raw.strip()
                if not raw:
                    return None
            try:
                value = int(raw)
            except (TypeError, ValueError):
                return None
            if value <= 0:
                return None
            return value

        channel_id = _parse_channel_id(data.get("ch"))
        if channel_id is None:
            channel_id = _parse_channel_id(data.get("channel"))
        if channel_id is None:
            logger.warning(
                "chat.ws send drop reason=invalid_channel ch=%s channel=%s",
                data.get("ch"),
                data.get("channel"),
            )
            return
        channel = str(channel_id)
        if channel not in info.channels:
            logger.warning(
                "chat.ws send drop reason=not_subscribed channel=%s",
                channel_id,
            )
            return
        meta = info.metadata.get(channel)
        if meta is None:
            logger.warning(
                "chat.ws send drop reason=missing_meta channel=%s",
                channel_id,
            )
            return
        payload = data.get("d") or data.get("payload") or {}
        content = payload.get("content", "")
        attachments = payload.get("attachments") or []
        avatar_url = payload.get("avatar_url")
        use_character_name_flag = bool(
            payload.get("useCharacterName") or payload.get("use_character_name")
        )
        raw_embed_color = payload.get("embedColor")
        if raw_embed_color is None:
            raw_embed_color = payload.get("embed_color")
        embed_color_value: int | None = None
        if raw_embed_color is not None:
            try:
                embed_color_value = int(raw_embed_color)
            except (TypeError, ValueError):
                embed_color_value = None
        raw_embed_border = payload.get("embedBorder")
        if raw_embed_border is None:
            raw_embed_border = payload.get("embed_border")
        embed_border_value: Mapping[str, object] | None = None
        if isinstance(raw_embed_border, str):
            try:
                embed_border_value = json.loads(raw_embed_border)
            except json.JSONDecodeError:
                embed_border_value = None
        elif isinstance(raw_embed_border, Mapping):
            embed_border_value = raw_embed_border

        channel_obj: discord.abc.Messageable | discord.Thread | None = None
        thread_id: int | None = None
        thread_cls = getattr(discord, "Thread", None)
        if discord_client:
            channel_obj = discord_client.get_channel(channel_id)
            if thread_cls is not None and isinstance(channel_obj, thread_cls):
                thread_identifier = getattr(channel_obj, "id", None)
                try:
                    thread_id = int(thread_identifier) if thread_identifier is not None else channel_id
                except (TypeError, ValueError):
                    thread_id = channel_id

        def _normalize_channel_kind(value: object | None) -> ChannelKind:
            if isinstance(value, ChannelKind):
                return value
            if isinstance(value, str):
                lowered = value.lower()
                try:
                    return ChannelKind(lowered)
                except ValueError:
                    try:
                        return ChannelKind(value)
                    except ValueError:
                        return ChannelKind.FC_CHAT
            return ChannelKind.FC_CHAT

        raw_channel_kind_value: object | None = None
        normalized_channel_kind: ChannelKind = ChannelKind.FC_CHAT
        async with get_session() as db:
            result = await db.execute(
                select(GuildChannel.webhook_url, GuildChannel.kind).where(
                    GuildChannel.guild_id == info.ctx.guild.id,
                    GuildChannel.channel_id == channel_id,
                )
            )
            row = result.one_or_none()
            webhook_url = row[0] if row else None
            raw_channel_kind_value = row[1] if row else None
            normalized_channel_kind = _normalize_channel_kind(raw_channel_kind_value)
            if not webhook_url:
                if not discord_client:
                    logger.warning(
                        "chat.ws missing webhook channel=%s reason=no_client",
                        channel_id,
                    )
                    return
                channel_obj = channel_obj or discord_client.get_channel(channel_id)
                if not isinstance(channel_obj, discord.abc.Messageable):
                    logger.warning(
                        "chat.ws missing webhook channel=%s reason=no_channel",
                        channel_id,
                    )
                    return
                if (
                    thread_id is None
                    and thread_cls is not None
                    and isinstance(channel_obj, thread_cls)
                ):
                    thread_identifier = getattr(channel_obj, "id", None)
                    try:
                        thread_id = (
                            int(thread_identifier)
                            if thread_identifier is not None
                            else channel_id
                        )
                    except (TypeError, ValueError):
                        thread_id = channel_id
                try:
                    _, created_url, creation_errors = await create_webhook_for_channel(
                        channel=channel_obj,
                        channel_id=channel_id,
                        guild_id=info.ctx.guild.id,
                        db=db,
                        channel_kind=normalized_channel_kind,
                        configured_channel_id=channel_id,
                    )
                except HTTPException:
                    logger.warning(
                        "chat.ws missing webhook channel=%s reason=forbidden",
                        channel_id,
                    )
                    return
                if creation_errors:
                    logger.warning(
                        "chat.ws webhook creation errors channel=%s errors=%s",
                        channel_id,
                        creation_errors,
                    )
                if not created_url:
                    logger.warning(
                        "chat.ws missing webhook channel=%s", channel_id
                    )
                    return
                webhook_url = created_url
                await db.commit()
            membership = await db.scalar(
                select(Membership).where(
                    Membership.guild_id == info.ctx.guild.id,
                    Membership.user_id == info.ctx.user.id,
                )
            )
        channel_kind_value = normalized_channel_kind
        if raw_channel_kind_value is None and meta.kind:
            try:
                channel_kind_value = ChannelKind(meta.kind.lower())
            except ValueError:
                pass
        if (
            channel_kind_value == ChannelKind.OFFICER_CHAT
            and "officer" not in info.ctx.roles
        ):
            logger.warning(
                "chat.ws send drop reason=officer_required channel=%s",
                channel_id,
            )
            return
        if not webhook_url:
            logger.warning("chat.ws missing webhook channel=%s", channel_id)
            return
        _channel_webhooks[channel_id] = webhook_url
        if len(attachments) > MAX_ATTACHMENTS:
            logger.warning("chat.ws too many attachments channel=%s", channel_id)
            return
        file_data: List[tuple[str, bytes, str | None]] = []
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
            content_type = a.get("contentType") or a.get("content_type")
            file_data.append((a.get("filename", "file"), file_bytes, content_type))
        bridge_content, embed_objects, uploads, nonce = build_bridge_message(
            content=content,
            user=info.ctx.user,
            membership=membership,
            channel_kind=channel_kind_value,
            use_character_name=use_character_name_flag,
            attachments=file_data,
            embed_color=embed_color_value,
            embed_border=embed_border_value,
        )

        username = (
            f"{info.ctx.user.character_name} (DemiCat)"
            if info.ctx.user.character_name
            else "DemiCat"
        )
        payload_with_nonce = dict(payload)
        payload_with_nonce.setdefault("nonce", nonce)
        msg = PendingWebhookMessage(
            channel_id=channel_id,
            webhook_url=webhook_url,
            content=bridge_content,
            username=username,
            avatar_url=avatar_url,
            uploads=uploads,
            embeds=embed_objects,
            nonce=nonce,
            payload=payload_with_nonce,
            thread_id=thread_id,
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
