import asyncio
import itertools
import json
import types
import pytest
from collections import deque
from contextlib import asynccontextmanager
from fastapi import HTTPException, WebSocketDisconnect
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "demibot"))

from demibot.http import ws_chat
from demibot.http.deps import RequestContext


class StubWebSocket:
    def __init__(self, token: str | None = "token"):
        self.scope = {"path": "/ws/chat"}
        self.headers = {"X-Api-Key": token} if token is not None else {}
        self.query_params = {}
        self.close_code: int | None = None
        self.close_reason: str | None = None
        self.sent: list[str] = []
        self.messages: list[dict] = []

    async def accept(self, headers=None):  # pragma: no cover - noop
        pass

    async def send_text(self, message: str):
        self.sent.append(message)

    async def close(self, code: int, reason: str):
        self.close_code = code
        self.close_reason = reason

    async def receive_json(self):
        if self.messages:
            return self.messages.pop(0)
        raise WebSocketDisconnect()

    async def ping(self):
        return None


class DummySession:
    async def execute(self, *args, **kwargs):
        return types.SimpleNamespace(
            all=lambda: [(1, 1, "CHAT", 1)]
        )

    async def close(self):  # pragma: no cover - trivial
        pass


def _run(coro):
    asyncio.run(coro)


def test_chat_ws_role_access(monkeypatch):
    async def scenario(roles, expect_resync):
        ws = StubWebSocket()
        ws.messages = [{"op": "sub", "channels": [{"id": "1", "officer": True}]}]
        guild = types.SimpleNamespace(id=1, discord_guild_id=1)
        user = types.SimpleNamespace(id=1, discord_user_id=1, character_name="a")
        ctx = RequestContext(user=user, guild=guild, key=None, roles=roles)

        @asynccontextmanager
        async def fake_get_session():
            yield DummySession()

        async def fake_auth(request, x_api_key, x_discord_id, db):
            assert request.method == "WS"
            return ctx

        manager = ws_chat.ChatConnectionManager()
        monkeypatch.setattr(ws_chat, "get_session", fake_get_session)
        monkeypatch.setattr(ws_chat, "api_key_auth", fake_auth)
        monkeypatch.setattr(ws_chat, "manager", manager)

        await ws_chat.websocket_endpoint_chat(ws)
        if expect_resync:
            assert ws.sent
            assert any(json.loads(msg)["op"] == "resync" for msg in ws.sent)
        else:
            assert ws.sent == []

    _run(scenario(["officer"], True))
    _run(scenario([], False))


def test_chat_ws_invalid_token(monkeypatch):
    async def scenario():
        ws = StubWebSocket(token="bad")

        @asynccontextmanager
        async def fake_get_session():
            yield DummySession()

        async def fake_auth(request, x_api_key, x_discord_id, db):
            assert request.method == "WS"
            raise HTTPException(status_code=401, detail="bad")

        monkeypatch.setattr(ws_chat, "get_session", fake_get_session)
        monkeypatch.setattr(ws_chat, "api_key_auth", fake_auth)

        await ws_chat.websocket_endpoint_chat(ws)
        assert ws.close_code == 1008
        assert ws.close_reason == "auth failed"

    _run(scenario())


def test_chat_ws_missing_token():
    async def scenario():
        ws = StubWebSocket(token=None)
        await ws_chat.websocket_endpoint_chat(ws)
        assert ws.close_code == 1008
        assert ws.close_reason == "missing token"

    _run(scenario())


def test_chat_ws_frames_include_metadata(monkeypatch):
    async def scenario():
        manager = ws_chat.ChatConnectionManager()
        ws = StubWebSocket()
        meta = ws_chat.ChannelMeta(guild_id=1, discord_guild_id=321, kind="CHAT")

        await manager._send_subscription_ack(ws, "42", meta)
        ack = json.loads(ws.sent[-1])
        assert ack["guildId"] == "321"
        assert ack["kind"] == "CHAT"

        await manager._send_resync(ws, "42", meta)
        resync = json.loads(ws.sent[-1])
        assert resync["guildId"] == "321"
        assert resync["kind"] == "CHAT"

        ws.sent.clear()

        ctx = RequestContext(
            user=types.SimpleNamespace(character_name=None),
            guild=types.SimpleNamespace(id=1, discord_guild_id=321),
            key=types.SimpleNamespace(),
            roles=[],
        )
        info = ws_chat.ChatConnection(ctx=ctx)
        info.channels.add("42")
        info.metadata["42"] = meta
        manager.connections[ws] = info
        manager._channel_meta["42"] = meta
        manager._channel_queues["42"] = [{"cursor": 7, "op": "mc", "d": {}}]

        async def fake_sleep(delay):
            return None

        monkeypatch.setattr(ws_chat.asyncio, "sleep", fake_sleep)
        monkeypatch.setattr(ws_chat.random, "uniform", lambda a, b: 0.0)

        await manager._flush_channel("42")
        batch = json.loads(ws.sent[-1])
        assert batch["guildId"] == "321"
        assert batch["kind"] == "CHAT"

    _run(scenario())


def test_chat_ws_send_uses_channel_field(monkeypatch):
    async def scenario():
        ws_chat._channel_webhooks.clear()
        manager = ws_chat.ChatConnectionManager()
        ws = StubWebSocket()
        ctx = RequestContext(
            user=types.SimpleNamespace(
                id=7, discord_user_id=77, character_name="Tester"
            ),
            guild=types.SimpleNamespace(id=3, discord_guild_id=333),
            key=None,
            roles=[],
        )
        manager.connections[ws] = ws_chat.ChatConnection(ctx=ctx)

        class FakeSession:
            def __init__(self):
                self.execute_calls = 0

            async def execute(self, *args, **kwargs):
                self.execute_calls += 1
                return types.SimpleNamespace(
                    one_or_none=lambda: (
                        "https://example.invalid/webhook",
                        ws_chat.ChannelKind.CHAT,
                    )
                )

            async def scalar(self, *args, **kwargs):
                return None

            async def commit(self):  # pragma: no cover - no commit path exercised
                pass

        fake_session = FakeSession()

        @asynccontextmanager
        async def fake_get_session():
            yield fake_session

        def fake_build_bridge_message(
            *,
            content,
            user,
            membership,
            channel_kind,
            use_character_name=False,
            attachments=None,
            nonce=None,
        ):
            return content, [], [], nonce or "nonce"

        queued: list[ws_chat.PendingWebhookMessage] = []

        async def fake_queue_webhook(self, msg):
            queued.append(msg)

        monkeypatch.setattr(ws_chat, "get_session", fake_get_session)
        monkeypatch.setattr(ws_chat, "build_bridge_message", fake_build_bridge_message)
        monkeypatch.setattr(
            manager,
            "_queue_webhook",
            types.MethodType(fake_queue_webhook, manager),
        )

        await manager._handle_send(
            ws,
            {
                "channel": "12345",
                "payload": {"content": "hello"},
            },
        )

        assert fake_session.execute_calls == 1
        assert queued, "expected webhook message to be queued"
        message = queued[0]
        assert message.channel_id == 12345
        assert ws_chat._channel_webhooks[12345] == "https://example.invalid/webhook"

    _run(scenario())


def test_chat_ws_mixed_channel_batch_rejected(monkeypatch):
    async def scenario():
        manager = ws_chat.ChatConnectionManager()
        ws = StubWebSocket()
        ctx = RequestContext(
            user=types.SimpleNamespace(character_name=None),
            guild=types.SimpleNamespace(id=1, discord_guild_id=1),
            key=types.SimpleNamespace(),
            roles=[],
        )
        info = ws_chat.ChatConnection(ctx=ctx)
        info.channels.add("42")
        info.metadata["42"] = ws_chat.ChannelMeta(guild_id=1, discord_guild_id=None, kind="FC_CHAT")
        manager.connections[ws] = info
        manager._channel_meta["42"] = ws_chat.ChannelMeta(guild_id=1, discord_guild_id=None, kind="CHAT")
        manager._channel_queues["42"] = [{"cursor": 1, "op": "mc", "d": {}}]

        async def fake_sleep(delay):
            return None

        monkeypatch.setattr(ws_chat.asyncio, "sleep", fake_sleep)
        monkeypatch.setattr(ws_chat.random, "uniform", lambda a, b: 0.0)

        await manager._flush_channel("42")
        assert ws.sent == []

    _run(scenario())


def test_chat_ws_subscription_prefers_specific_kind(monkeypatch):
    async def scenario():
        manager = ws_chat.ChatConnectionManager()
        ws = StubWebSocket()
        ctx = RequestContext(
            user=types.SimpleNamespace(character_name=None),
            guild=types.SimpleNamespace(id=1, discord_guild_id=777),
            key=types.SimpleNamespace(),
            roles=[],
        )
        manager.connections[ws] = ws_chat.ChatConnection(ctx=ctx)

        class MultiRowSession:
            async def execute(self, *args, **kwargs):
                return types.SimpleNamespace(
                    all=lambda: [
                        (123, 1, types.SimpleNamespace(value="chat"), 777),
                        (123, 1, types.SimpleNamespace(value="fc_chat"), 777),
                    ]
                )

            async def close(self):  # pragma: no cover - trivial
                pass

        @asynccontextmanager
        async def fake_get_session():
            yield MultiRowSession()

        monkeypatch.setattr(ws_chat, "get_session", fake_get_session)

        await manager.sub(
            ws,
            {
                "channels": [
                    {
                        "id": "123",
                        "kind": "FC_CHAT",
                    }
                ]
            },
        )

        info = manager.connections[ws]
        assert "123" in info.channels
        meta = info.metadata["123"]
        assert meta.kind == "FC_CHAT"
        assert manager._channel_meta["123"].kind == "FC_CHAT"
        assert len(ws.sent) == 2
        ack = json.loads(ws.sent[0])
        resync = json.loads(ws.sent[1])
        assert ack["kind"] == "FC_CHAT"
        assert resync["kind"] == "FC_CHAT"
        assert ack["guildId"] == "777"
        assert resync["guildId"] == "777"

    _run(scenario())


def test_chat_ws_subscription_backfill(monkeypatch):
    async def scenario():
        manager = ws_chat.ChatConnectionManager()
        ws = StubWebSocket()
        ctx = RequestContext(
            user=types.SimpleNamespace(character_name="tester"),
            guild=types.SimpleNamespace(id=1, discord_guild_id=999),
            key=types.SimpleNamespace(),
            roles=[],
        )
        manager.connections[ws] = ws_chat.ChatConnection(ctx=ctx)
        channel_id = "123"
        meta = ws_chat.ChannelMeta(guild_id=1, discord_guild_id=999, kind="CHAT")
        manager._channel_history[channel_id] = deque(
            [
                {"cursor": 1, "op": "mc", "d": {"id": "old"}},
                {"cursor": 2, "op": "mc", "d": {"id": "new1"}},
                {"cursor": 3, "op": "mc", "d": {"id": "new2"}},
            ]
        )
        manager._channel_cursors[channel_id] = 3

        async def fake_fetch(channels):
            assert channels == {channel_id}
            manager._channel_meta[channel_id] = meta
            return {channel_id: meta}

        monkeypatch.setattr(manager, "_fetch_channel_meta_bulk", fake_fetch)

        await manager.sub(
            ws,
            {
                "channels": [
                    {
                        "id": channel_id,
                        "since": 1,
                    }
                ]
            },
        )

        assert len(ws.sent) == 3
        backfill = json.loads(ws.sent[0])
        assert backfill["op"] == "batch"
        assert backfill["channel"] == channel_id
        assert backfill["guildId"] == "999"
        assert [msg["cursor"] for msg in backfill["messages"]] == [2, 3]
        assert ws.sent[1] != ws.sent[0]
        assert json.loads(ws.sent[1])["op"] == "ack"
        assert json.loads(ws.sent[2])["op"] == "resync"

    _run(scenario())


def test_chat_ws_history_cap(monkeypatch):
    async def scenario():
        manager = ws_chat.ChatConnectionManager()
        ws = StubWebSocket()
        ctx = RequestContext(
            user=types.SimpleNamespace(character_name=None),
            guild=types.SimpleNamespace(id=1, discord_guild_id=555),
            key=types.SimpleNamespace(),
            roles=[],
        )
        info = ws_chat.ChatConnection(ctx=ctx)
        channel_id = "55"
        meta = ws_chat.ChannelMeta(guild_id=1, discord_guild_id=555, kind="CHAT")
        info.channels.add(channel_id)
        info.metadata[channel_id] = meta
        manager.connections[ws] = info
        manager._channel_meta[channel_id] = meta

        total = ws_chat.HISTORY_LIMIT + 5
        manager._channel_queues[channel_id] = [
            {"cursor": idx + 1, "op": "mc", "d": {"id": idx + 1}}
            for idx in range(total)
        ]

        async def fake_sleep(delay):
            return None

        monkeypatch.setattr(ws_chat.asyncio, "sleep", fake_sleep)
        monkeypatch.setattr(ws_chat.random, "uniform", lambda a, b: 0.0)

        await manager._flush_channel(channel_id)

        history = manager._channel_history[channel_id]
        assert len(history) == ws_chat.HISTORY_LIMIT
        assert [msg["cursor"] for msg in history] == list(
            range(total - ws_chat.HISTORY_LIMIT + 1, total + 1)
        )

    _run(scenario())


def test_chat_ws_unsubscribe_cleans_channel_state(monkeypatch):
    async def scenario():
        manager = ws_chat.ChatConnectionManager()
        ws = StubWebSocket()
        ctx = RequestContext(
            user=types.SimpleNamespace(character_name=None),
            guild=types.SimpleNamespace(id=1, discord_guild_id=1),
            key=types.SimpleNamespace(),
            roles=[],
        )
        manager.connections[ws] = ws_chat.ChatConnection(ctx=ctx)
        channel_id = "99"
        meta = ws_chat.ChannelMeta(guild_id=1, discord_guild_id=1, kind="CHAT")

        async def fake_fetch(channels):
            return {ch: meta for ch in channels}

        monkeypatch.setattr(manager, "_fetch_channel_meta_bulk", fake_fetch)

        await manager.sub(ws, {"channels": [{"id": channel_id}]})
        assert manager._channel_subscribers[channel_id] == 1

        manager._channel_history[channel_id] = deque([{"cursor": 1}])
        manager._channel_last_touch[channel_id] = 0.0
        manager._channel_queues[channel_id] = [{"cursor": 2}]
        loop = asyncio.get_running_loop()
        task = loop.create_task(asyncio.sleep(60))
        manager._channel_tasks[channel_id] = task
        manager._channel_meta[channel_id] = meta
        manager._channel_cursors[channel_id] = 7

        await manager.sub(ws, {"channels": []})
        await asyncio.sleep(0)

        assert channel_id not in manager._channel_history
        assert channel_id not in manager._channel_tasks
        assert channel_id not in manager._channel_queues
        assert channel_id not in manager._channel_meta
        assert channel_id not in manager._channel_cursors
        assert channel_id not in manager._channel_subscribers
        assert channel_id not in manager._channel_last_touch
        assert task.cancelled()

    _run(scenario())


def test_chat_ws_history_global_cap(monkeypatch):
    async def scenario():
        manager = ws_chat.ChatConnectionManager()
        meta = ws_chat.ChannelMeta(guild_id=1, discord_guild_id=1, kind="CHAT")

        async def fake_sleep(delay):
            return None

        monkeypatch.setattr(ws_chat.asyncio, "sleep", fake_sleep)
        monkeypatch.setattr(ws_chat.random, "uniform", lambda a, b: 0.0)
        clock = itertools.count()
        monkeypatch.setattr(ws_chat.time, "time", lambda: next(clock) / 10)

        total_channels = ws_chat.HISTORY_CHANNEL_CAP + 5
        for idx in range(total_channels):
            channel = str(idx)
            manager._channel_meta[channel] = meta
            manager._channel_queues[channel] = [
                {"cursor": 1, "op": "mc", "d": {"id": idx}}
            ]
            await manager._flush_channel(channel)

        assert len(manager._channel_history) <= ws_chat.HISTORY_CHANNEL_CAP
        remaining = {int(ch) for ch in manager._channel_history.keys()}
        assert remaining
        assert min(remaining) >= total_channels - ws_chat.HISTORY_CHANNEL_CAP

    _run(scenario())


def test_chat_ws_history_ttl(monkeypatch):
    async def scenario():
        manager = ws_chat.ChatConnectionManager()
        meta = ws_chat.ChannelMeta(guild_id=1, discord_guild_id=1, kind="CHAT")
        old_channel = "old"
        manager._channel_history[old_channel] = deque([{"cursor": 1}])
        manager._channel_last_touch[old_channel] = 0.0

        async def fake_sleep(delay):
            return None

        monkeypatch.setattr(ws_chat.asyncio, "sleep", fake_sleep)
        monkeypatch.setattr(ws_chat.random, "uniform", lambda a, b: 0.0)
        current_time = ws_chat.HISTORY_TTL_SECONDS + 10
        monkeypatch.setattr(ws_chat.time, "time", lambda: current_time)

        new_channel = "new"
        manager._channel_meta[new_channel] = meta
        manager._channel_queues[new_channel] = [
            {"cursor": 1, "op": "mc", "d": {"id": "fresh"}}
        ]

        await manager._flush_channel(new_channel)

        assert old_channel not in manager._channel_history
        assert manager._channel_last_touch.get(old_channel) == 0.0

    _run(scenario())


def test_chat_ws_idle_purge(monkeypatch):
    manager = ws_chat.ChatConnectionManager()
    channel_id = "314"
    manager._channel_history[channel_id] = deque([{"cursor": 1}])
    manager._channel_last_touch[channel_id] = 0.0
    manager._channel_meta[channel_id] = ws_chat.ChannelMeta(
        guild_id=1, discord_guild_id=1, kind="CHAT"
    )
    manager._channel_cursors[channel_id] = 3

    monkeypatch.setattr(
        ws_chat.time,
        "time",
        lambda: ws_chat.HISTORY_TTL_SECONDS + 20,
    )

    manager._purge_idle_channels()

    assert channel_id not in manager._channel_history
    assert channel_id not in manager._channel_meta
    assert channel_id not in manager._channel_cursors
