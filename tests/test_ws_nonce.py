import asyncio

import pytest

import sys
import types
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "demibot"
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

if "structlog" not in sys.modules:
    def _callable_stub(*args, **kwargs):
        return None

    def _factory_stub(*args, **kwargs):
        return _callable_stub

    sys.modules["structlog"] = types.SimpleNamespace(
        processors=types.SimpleNamespace(
            TimeStamper=lambda **kwargs: _callable_stub,
            add_log_level=_callable_stub,
            EventRenamer=lambda *args, **kwargs: _callable_stub,
            JSONRenderer=lambda *args, **kwargs: _callable_stub,
        ),
        make_filtering_bound_logger=lambda *args, **kwargs: _callable_stub,
        stdlib=types.SimpleNamespace(LoggerFactory=_factory_stub),
        configure=_callable_stub,
        get_logger=lambda *args, **kwargs: types.SimpleNamespace(
            info=_callable_stub,
            warning=_callable_stub,
            exception=_callable_stub,
            debug=_callable_stub,
        ),
    )

if "discord" not in sys.modules:
    class _DummyHTTPException(Exception):
        pass

    class _DummyWebhook:
        @classmethod
        def from_url(cls, *args, **kwargs):
            return cls()

        async def send(self, *args, **kwargs):
            return types.SimpleNamespace()

    class _DummyFile:
        def __init__(self, *args, **kwargs):
            pass

    class _DummyColor:
        def __init__(self, *args, **kwargs):
            self.value = args[0] if args else None

    class _DummyEmbed:
        def __init__(self, *args, **kwargs):
            self._data = {}

        def set_footer(self, **kwargs):
            self._data.setdefault("footer", {}).update(kwargs)

        def set_author(self, **kwargs):
            self._data.setdefault("author", {}).update(kwargs)

        def set_image(self, **kwargs):
            self._data.setdefault("image", {}).update(kwargs)

        def to_dict(self):
            return self._data.copy()

    discord_module = types.ModuleType("discord")
    discord_module.HTTPException = _DummyHTTPException
    discord_module.Webhook = _DummyWebhook
    discord_module.File = _DummyFile
    discord_module.Color = _DummyColor
    discord_module.Embed = _DummyEmbed
    discord_module.abc = types.SimpleNamespace(Messageable=object)
    discord_module.errors = types.SimpleNamespace(Forbidden=_DummyHTTPException)
    sys.modules["discord"] = discord_module

    discord_ext = types.ModuleType("discord.ext")
    discord_commands = types.ModuleType("discord.ext.commands")
    discord_commands.Bot = object
    discord_commands.Cog = object
    discord_commands.command = lambda *args, **kwargs: (lambda func: func)
    discord_ext.commands = discord_commands
    sys.modules["discord.ext"] = discord_ext
    sys.modules["discord.ext.commands"] = discord_commands

if "fastapi" not in sys.modules:
    class _DummyFastAPI:
        def __init__(self, *args, **kwargs):
            pass

        def add_middleware(self, *args, **kwargs):
            return None

        def add_api_websocket_route(self, *args, **kwargs):
            return None

        def include_router(self, *args, **kwargs):
            return None

        def get(self, *args, **kwargs):
            def decorator(func):
                return func

            return decorator

    class _DummyHTTPExceptionFastapi(Exception):
        pass

    class _DummyWebSocket:
        async def accept(self, *args, **kwargs):
            return None

        async def close(self, *args, **kwargs):
            return None

        async def send_text(self, *args, **kwargs):
            return None

        async def ping(self, *args, **kwargs):
            return None

    class _DummyWebSocketDisconnect(Exception):
        pass

    def _depends_stub(*args, **kwargs):
        return None

    def _header_stub(*args, **kwargs):
        return None

    sys.modules["fastapi"] = types.SimpleNamespace(
        FastAPI=_DummyFastAPI,
        HTTPException=_DummyHTTPExceptionFastapi,
        WebSocket=_DummyWebSocket,
        WebSocketDisconnect=_DummyWebSocketDisconnect,
        Depends=_depends_stub,
        Header=_header_stub,
        status=types.SimpleNamespace(HTTP_401_UNAUTHORIZED=401),
        Request=object,
    )

if "starlette.middleware.base" not in sys.modules:
    sys.modules["starlette.middleware.base"] = types.SimpleNamespace(
        BaseHTTPMiddleware=object
    )

if "starlette.requests" not in sys.modules:
    sys.modules["starlette.requests"] = types.SimpleNamespace(Request=object)

if "sqlalchemy" not in sys.modules:
    def _select_stub(*args, **kwargs):
        return None

    sys.modules["sqlalchemy"] = types.SimpleNamespace(select=_select_stub)

if "demibot.db.models" not in sys.modules:
    class _DummyGuildChannel:
        channel_id = None
        guild_id = None
        kind = None

    class _DummyGuild:
        id = None

    class _DummyChannelKind:
        OFFICER_CHAT = "officer"
        FC_CHAT = "fc"

    class _DummyMembership:
        pass

    sys.modules["demibot.db.models"] = types.SimpleNamespace(
        GuildChannel=_DummyGuildChannel,
        Guild=_DummyGuild,
        ChannelKind=_DummyChannelKind,
        Membership=_DummyMembership,
    )

if "demibot.db.session" not in sys.modules:
    class _DummySessionContext:
        async def __aenter__(self):
            return types.SimpleNamespace()

        async def __aexit__(self, exc_type, exc, tb):
            return False

    def _get_session_stub():
        return _DummySessionContext()

    sys.modules["demibot.db.session"] = types.SimpleNamespace(
        get_session=_get_session_stub
    )

if "demibot.http.deps" not in sys.modules:
    @dataclass
    class _DummyRequestContext:
        guild: types.SimpleNamespace = field(
            default_factory=lambda: types.SimpleNamespace(id=0)
        )
        roles: list[str] = field(default_factory=list)

    async def _api_key_auth_stub(*args, **kwargs):
        return _DummyRequestContext()

    sys.modules["demibot.http.deps"] = types.SimpleNamespace(
        RequestContext=_DummyRequestContext,
        api_key_auth=_api_key_auth_stub,
    )

if "demibot.http.discord_helpers" not in sys.modules:
    sys.modules["demibot.http.discord_helpers"] = types.SimpleNamespace(
        serialize_message=lambda message: (message, None)
    )

if "demibot.http.discord_client" not in sys.modules:
    sys.modules["demibot.http.discord_client"] = types.SimpleNamespace(
        discord_client=None
    )

if "demibot.http.chat_events" not in sys.modules:
    async def _emit_event_stub(*args, **kwargs):
        return None

    sys.modules["demibot.http.chat_events"] = types.SimpleNamespace(
        emit_event=_emit_event_stub
    )

if "demibot.http.routes._messages_common" not in sys.modules:
    async def _create_webhook_stub(*args, **kwargs):
        return None, None, []

    sys.modules["demibot.http.routes._messages_common"] = types.SimpleNamespace(
        create_webhook_for_channel=_create_webhook_stub,
        _channel_webhooks={},
    )

if "demibot.bridge" not in sys.modules:
    class _DummyBridgeUpload:
        def __init__(self, *args, **kwargs):
            pass

    def _build_bridge_message_stub(*args, **kwargs):
        return "", [], [], ""

    def _extract_bridge_nonce_stub(payload):
        nonce = payload.get("nonce") if isinstance(payload, dict) else None
        return str(nonce) if nonce is not None else None

    sys.modules["demibot.bridge"] = types.SimpleNamespace(
        BridgeUpload=_DummyBridgeUpload,
        build_bridge_message=_build_bridge_message_stub,
        extract_bridge_nonce_from_payload=_extract_bridge_nonce_stub,
        BRIDGE_MARKER="bridge:demicat nonce:",
    )

BRIDGE_MARKER = "bridge:demicat nonce:"
from demibot.http.ws_chat import ChatConnectionManager


def test_should_drop_due_to_nonce_and_cleanup(monkeypatch):
    manager = ChatConnectionManager()

    flush_calls: list[str] = []

    async def fake_flush(self, channel: str) -> None:
        flush_calls.append(channel)

    monkeypatch.setattr(
        ChatConnectionManager,
        "_flush_channel",
        fake_flush,
    )

    create_payload = {
        "op": "mc",
        "d": {
            "id": "42",
            "content": "hello",
            "nonce": "abc123",
            "embeds": [
                {
                    "footer": {"text": f"DemiCat • Chat • {BRIDGE_MARKER}abc123"},
                }
            ],
        },
    }

    update_payload = {
        "op": "mu",
        "d": {
            "id": "42",
            "content": "hello (edited)",
            "nonce": "abc123",
        },
    }

    history_payload = {
        "op": "history",
        "d": {
            "id": "42",
            "content": "hello",
            "nonce": "abc123",
        },
    }

    async def run_scenario() -> None:
        await manager.send("100", create_payload)
        await asyncio.sleep(0)
        assert "100" in manager._channel_nonce_cache
        assert len(manager._channel_queues.get("100", [])) == 1

        await manager.send("100", create_payload)
        await asyncio.sleep(0)
        assert len(manager._channel_queues.get("100", [])) == 1

        await manager.send("100", update_payload)
        await asyncio.sleep(0)
        queue = manager._channel_queues.get("100", [])
        assert len(queue) == 2
        assert queue[-1]["op"] == "mu"

        await manager.send("100", update_payload)
        await asyncio.sleep(0)
        assert len(manager._channel_queues.get("100", [])) == 2

        await manager.send("100", history_payload)
        await asyncio.sleep(0)
        assert len(manager._channel_queues.get("100", [])) == 3

        await manager.send("100", history_payload)
        await asyncio.sleep(0)
        assert len(manager._channel_queues.get("100", [])) == 3

        manager._cleanup_channel("100")
        assert "100" not in manager._channel_nonce_cache
        assert "100" not in manager._channel_nonce_order
        assert "100" not in manager._channel_queues

    asyncio.run(run_scenario())
