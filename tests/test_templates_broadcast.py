import json
import logging
import sys
import types
from pathlib import Path

import pytest

# Setup module paths similar to other template tests
root = Path(__file__).resolve().parents[1] / "demibot"
if str(root) not in sys.path:
    sys.path.append(str(root))

# Package stubs to avoid needing the full environment when importing demibot modules
# These stubs mirror the lightweight setup from tests/test_templates_sync.py

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)
http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

# Minimal RequestContext stub
deps_pkg = types.ModuleType("demibot.http.deps")


class RequestContext:
    def __init__(self, guild_id: int, roles: list[str] | None = None):
        self.guild = types.SimpleNamespace(id=guild_id)
        self.roles = roles or []


def api_key_auth(*args, **kwargs):  # pragma: no cover - compatibility stub
    raise RuntimeError("api_key_auth should not be called during unit tests")


deps_pkg.RequestContext = RequestContext
deps_pkg.api_key_auth = api_key_auth
deps_pkg.get_db = lambda: None
sys.modules.setdefault("demibot.http.deps", deps_pkg)

# Minimal discord stub to satisfy imports in demibot.http.routes.templates
sys.modules.setdefault("discord", types.ModuleType("discord"))

# Ensure any lingering FastAPI stubs from other tests don't interfere with the real package
sys.modules.pop("fastapi", None)
sys.modules.pop("fastapi.responses", None)

from demibot.http.routes import templates


class StubContext(RequestContext):
    """Reuses the lightweight context fixture from the sync tests."""


@pytest.fixture
def anyio_backend():
    return "asyncio"


@pytest.mark.anyio
async def test_broadcast_template_update_logs_success_and_failure(monkeypatch, caplog):
    ctx = StubContext(guild_id=42)
    payload = {"id": "abc123"}
    channel_id = 1234
    captured: dict[str, tuple] = {}

    async def fake_broadcast(message: str, guild_id: int, path: str):
        captured["call"] = (message, guild_id, path)

    monkeypatch.setattr(templates.manager, "broadcast_text", fake_broadcast)
    caplog.set_level(logging.INFO, logger=templates.logger.name)

    await templates._broadcast_template_update(ctx, channel_id, payload)

    assert "call" in captured
    message, guild_id, path = captured["call"]
    assert guild_id == ctx.guild.id
    assert path == "/ws/templates"
    assert json.loads(message)["payload"] == payload

    info_records = [r for r in caplog.records if r.levelno == logging.INFO]
    assert info_records, "Expected an info log for successful broadcast"
    record = info_records[-1]
    assert record.message == "Websocket broadcast successful"
    assert record.guild_id == ctx.guild.id
    assert record.channel_id == channel_id

    caplog.clear()

    class BroadcastError(Exception):
        pass

    async def failing_broadcast(*_args, **_kwargs):
        raise BroadcastError("boom")

    monkeypatch.setattr(templates.manager, "broadcast_text", failing_broadcast)
    caplog.set_level(logging.ERROR, logger=templates.logger.name)

    # Should not raise, but should log the failure path
    await templates._broadcast_template_update(ctx, channel_id, payload)

    error_records = [r for r in caplog.records if r.levelno == logging.ERROR]
    assert error_records, "Expected an error log for failed broadcast"
    error_record = error_records[-1]
    assert error_record.message == "Websocket broadcast failed"
    assert error_record.guild_id == ctx.guild.id
    assert error_record.channel_id == channel_id
    assert error_record.error == "boom"
