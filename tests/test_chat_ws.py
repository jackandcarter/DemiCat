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

discord_stub = types.ModuleType("discord")
discord_stub.Embed = type("Embed", (), {})
discord_stub.HTTPException = type("HTTPException", (Exception,), {})
discord_stub.Forbidden = type("Forbidden", (Exception,), {})
discord_stub.NotFound = type("NotFound", (Exception,), {})
discord_stub.ClientException = type("ClientException", (Exception,), {})
discord_stub.Webhook = type(
    "Webhook",
    (),
    {
        "from_url": staticmethod(
            lambda url, client: types.SimpleNamespace(send=lambda *args, **kwargs: None)
        )
    },
)
discord_stub.abc = types.SimpleNamespace(Messageable=object)
discord_commands_stub = types.ModuleType("discord.ext.commands")
discord_commands_stub.Bot = type("Bot", (), {})
discord_ext_stub = types.ModuleType("discord.ext")
discord_ext_stub.commands = discord_commands_stub
discord_stub.ext = discord_ext_stub
sys.modules.setdefault("discord", discord_stub)
sys.modules.setdefault("discord.ext", discord_ext_stub)
sys.modules.setdefault("discord.ext.commands", discord_commands_stub)

structlog_stub = types.ModuleType("structlog")
structlog_stub.get_logger = lambda *args, **kwargs: None
sys.modules.setdefault("structlog", structlog_stub)

alembic_stub = types.ModuleType("alembic")
alembic_command_stub = types.ModuleType("alembic.command")
alembic_command_stub.upgrade = lambda *args, **kwargs: None
alembic_stub.command = alembic_command_stub
sys.modules.setdefault("alembic", alembic_stub)
sys.modules.setdefault("alembic.command", alembic_command_stub)

class _AlembicConfig:
    def __init__(self):
        self.options: dict[str, str] = {}

    def set_main_option(self, key: str, value: str) -> None:
        self.options[key] = value


alembic_config_stub = types.ModuleType("alembic.config")
alembic_config_stub.Config = _AlembicConfig
sys.modules.setdefault("alembic.config", alembic_config_stub)

allowed_mentions_stub = types.ModuleType("demibot.http.discord_allowed_mentions")


class _AllowedMentionsStub:
    users = True
    roles = True
    everyone = False

    def to_dict(self) -> dict[str, object]:  # pragma: no cover - simple stub
        return {"users": [], "roles": [], "everyone": False}


allowed_mentions_stub.ALLOWED_MENTIONS = _AllowedMentionsStub()
sys.modules.setdefault("demibot.http.discord_allowed_mentions", allowed_mentions_stub)

messages_common_stub = types.ModuleType("demibot.http.routes._messages_common")
messages_common_stub._channel_webhooks = {}
messages_common_stub.create_webhook_for_channel = lambda *args, **kwargs: None
messages_common_stub.ALLOWED_MENTIONS = allowed_mentions_stub.ALLOWED_MENTIONS
routes_stub = types.ModuleType("demibot.http.routes")
routes_stub.__path__ = []
routes_stub._messages_common = messages_common_stub
sys.modules.setdefault("demibot.http.routes", routes_stub)
sys.modules.setdefault("demibot.http.routes._messages_common", messages_common_stub)

discord_helpers_stub = types.ModuleType("demibot.http.discord_helpers")
discord_helpers_stub.serialize_message = lambda message: (message, [])
sys.modules.setdefault("demibot.http.discord_helpers", discord_helpers_stub)

discord_client_stub = types.ModuleType("demibot.http.discord_client")
discord_client_stub.discord_client = None
discord_client_stub.set_discord_client = lambda client: None
discord_client_stub.is_discord_client_ready = lambda client=None: False
sys.modules.setdefault("demibot.http.discord_client", discord_client_stub)

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
            all=lambda: [(1, 1, ws_chat.OFFICER_CHAT_KIND, 1)]
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


def test_chat_ws_ping_fallback_sends_heartbeat():
    async def scenario():
        manager = ws_chat.ChatConnectionManager()

        class NoPingWebSocket:
            def __init__(self) -> None:
                self.scope = {"path": "/ws/chat"}
                self.sent: list[str] = []

            async def send_text(self, message: str) -> None:
                self.sent.append(message)

        ws = NoPingWebSocket()
        ctx = RequestContext(
            user=types.SimpleNamespace(id=1, discord_user_id=1, character_name="Tester"),
            guild=types.SimpleNamespace(id=1, discord_guild_id=1),
            key=None,
            roles=[],
        )
        manager.connections[ws] = ws_chat.ChatConnection(ctx=ctx)

        alive = await manager._probe_connection(ws)

        assert alive is True
        assert ws.sent == [ws_chat.HEARTBEAT_PAYLOAD]

    _run(scenario())


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
        info = manager.connections[ws]
        info.channels.add("12345")
        info.metadata["12345"] = ws_chat.ChannelMeta(
            guild_id=ctx.guild.id,
            discord_guild_id=ctx.guild.discord_guild_id,
            kind="FC_CHAT",
        )

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


def test_chat_ws_send_requires_subscription(monkeypatch):
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

        get_session_called = False

        @asynccontextmanager
        async def fake_get_session():
            nonlocal get_session_called
            get_session_called = True
            yield types.SimpleNamespace()

        async def fake_queue_webhook(self, msg):  # pragma: no cover - not expected
            raise AssertionError("queue should not be called")

        monkeypatch.setattr(ws_chat, "get_session", fake_get_session)
        monkeypatch.setattr(
            manager,
            "_queue_webhook",
            types.MethodType(fake_queue_webhook, manager),
        )

        await manager._handle_send(
            ws,
            {
                "channel": 12345,
                "payload": {"content": "hello"},
            },
        )

        assert get_session_called is False

    _run(scenario())


def test_chat_ws_officer_send_requires_role(monkeypatch):
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
        info = manager.connections[ws]
        info.channels.add("1")
        info.metadata["1"] = ws_chat.ChannelMeta(
            guild_id=ctx.guild.id,
            discord_guild_id=ctx.guild.discord_guild_id,
            kind=ws_chat.OFFICER_CHAT_KIND,
        )

        class DummySession:
            async def execute(self, *args, **kwargs):
                return types.SimpleNamespace(
                    one_or_none=lambda: (
                        "https://example.invalid/webhook",
                        ws_chat.ChannelKind.OFFICER_CHAT,
                    )
                )

            async def scalar(self, *args, **kwargs):
                return None

            async def commit(self):  # pragma: no cover - commit not expected
                raise AssertionError("commit should not be called")

            async def close(self):  # pragma: no cover - trivial
                pass

        session = DummySession()

        @asynccontextmanager
        async def fake_get_session():
            yield session

        def fake_build_bridge_message(**kwargs):  # pragma: no cover - not expected
            raise AssertionError("build_bridge_message should not be called")

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
                "channel": 1,
                "payload": {"content": "hello"},
            },
        )

        assert queued == []
        assert 1 not in ws_chat._channel_webhooks

    _run(scenario())


def test_chat_ws_recovers_from_stale_webhook(monkeypatch):
    async def scenario():
        ws_chat._channel_webhooks.clear()
        manager = ws_chat.ChatConnectionManager()
        channel_id = 9876
        manager._channel_meta[str(channel_id)] = ws_chat.ChannelMeta(
            guild_id=5, discord_guild_id=50, kind="CHAT"
        )

        class FakeSession:
            def __init__(self):
                self.commit_called = False
                self.rollback_called = False
                self.cleared = False
                self.row = self._make_row()

            def _make_row(self):
                session = self

                class Row:
                    def __init__(self) -> None:
                        self.guild_id = 5
                        self.channel_id = channel_id
                        self.kind = ws_chat.ChannelKind.CHAT
                        self._webhook_url = "https://example.invalid/stale"

                    @property
                    def webhook_url(self):
                        return self._webhook_url

                    @webhook_url.setter
                    def webhook_url(self, value):
                        if value is None:
                            session.cleared = True
                        self._webhook_url = value

                return Row()

            async def execute(self, *args, **kwargs):
                return types.SimpleNamespace(
                    scalars=lambda: types.SimpleNamespace(all=lambda: [self.row])
                )

            async def commit(self):
                self.commit_called = True

            async def rollback(self):
                self.rollback_called = True

            async def close(self):  # pragma: no cover - close not triggered
                pass

        fake_session = FakeSession()

        @asynccontextmanager
        async def fake_get_session():
            yield fake_session

        events: list[dict] = []

        async def fake_emit_event(event):
            events.append(event)

        counter = itertools.count(100)

        class DummyDiscordMessage:
            def __init__(self):
                self.id = next(counter)
                self.attachments = []

            def model_dump(self, **kwargs):  # pragma: no cover - defensive
                return {"id": self.id}

        class FreshWebhook:
            def __init__(self) -> None:
                self.calls: list[dict[str, object]] = []

            async def send(
                self,
                content,
                *,
                username=None,
                avatar_url=None,
                files=None,
                embeds=None,
                wait=True,
                allowed_mentions=None,
            ):
                self.calls.append(
                    {
                        "content": content,
                        "username": username,
                        "avatar_url": avatar_url,
                        "files": files,
                        "embeds": embeds,
                        "wait": wait,
                        "allowed_mentions": allowed_mentions,
                    }
                )
                return DummyDiscordMessage()

        fresh_webhook = FreshWebhook()

        class FailingWebhook:
            def __init__(self) -> None:
                self.calls = 0

            async def send(self, *args, **kwargs):
                self.calls += 1
                exc = ws_chat.discord.HTTPException("gone")
                exc.status = 404
                exc.response = types.SimpleNamespace(headers={})
                raise exc

        failing_webhook = FailingWebhook()
        created_urls: list[str] = []

        async def fake_create_webhook_for_channel(**kwargs):
            created_urls.append("https://example.invalid/fresh")
            fake_session.row.webhook_url = "https://example.invalid/fresh"
            ws_chat._channel_webhooks[channel_id] = "https://example.invalid/fresh"
            return fresh_webhook, "https://example.invalid/fresh", []

        def fake_from_url(url, client=None):
            if url == "https://example.invalid/stale":
                return failing_webhook
            assert url == "https://example.invalid/fresh"
            return fresh_webhook

        def fake_serialize(message):
            class DummyDto:
                def model_dump(self, **kwargs):
                    return {"id": message.id}

            return DummyDto(), []

        monkeypatch.setattr(ws_chat, "get_session", fake_get_session)
        monkeypatch.setattr(ws_chat, "emit_event", fake_emit_event)
        monkeypatch.setattr(ws_chat, "create_webhook_for_channel", fake_create_webhook_for_channel)
        monkeypatch.setattr(ws_chat.discord.Webhook, "from_url", staticmethod(fake_from_url))
        monkeypatch.setattr(ws_chat, "serialize_message", fake_serialize)

        ws_chat._channel_webhooks[channel_id] = "https://example.invalid/stale"

        msg = ws_chat.PendingWebhookMessage(
            channel_id=channel_id,
            webhook_url="https://example.invalid/stale",
            content="hello",
            username="Tester",
            avatar_url=None,
            uploads=[],
            embeds=[],
            nonce="nonce",
            payload={"op": "mc"},
        )

        success, retry_after = await manager._send_webhook(msg)

        assert success is True
        assert retry_after == 0.0
        assert msg.webhook_url == "https://example.invalid/fresh"
        assert ws_chat._channel_webhooks[channel_id] == "https://example.invalid/fresh"
        assert fake_session.cleared is True
        assert fake_session.commit_called is True
        assert created_urls == ["https://example.invalid/fresh"]
        assert fresh_webhook.calls, "expected fresh webhook to be used"
        assert failing_webhook.calls == 1
        assert events and events[0]["channel"] == str(channel_id)

        msg2 = ws_chat.PendingWebhookMessage(
            channel_id=channel_id,
            webhook_url=msg.webhook_url,
            content="hello again",
            username="Tester",
            avatar_url=None,
            uploads=[],
            embeds=[],
            nonce="nonce2",
            payload={"op": "mc"},
        )

        success2, retry_after2 = await manager._send_webhook(msg2)

        assert success2 is True
        assert retry_after2 == 0.0
        assert len(created_urls) == 1, "webhook creation should only occur once"
        assert len(fresh_webhook.calls) == 2
        assert all(
            call["allowed_mentions"] is ws_chat.ALLOWED_MENTIONS
            for call in fresh_webhook.calls
        )

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
