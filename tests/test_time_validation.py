import pytest
import sys
import types
from pathlib import Path
from datetime import datetime, timezone
from fastapi import HTTPException

# Ensure the demo package is importable
root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))
demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)
http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

routes_pkg = types.ModuleType("demibot.http.routes")
routes_pkg.__path__ = [str(root / "demibot/http/routes")]
sys.modules.setdefault("demibot.http.routes", routes_pkg)

# Stub dependencies used during import
alembic_stub = types.ModuleType("alembic")
alembic_stub.command = types.SimpleNamespace()
sys.modules.setdefault("alembic", alembic_stub)
alembic_config_stub = types.ModuleType("alembic.config")
alembic_config_stub.Config = object
sys.modules.setdefault("alembic.config", alembic_config_stub)
deps_stub = types.ModuleType("demibot.http.deps")
deps_stub.RequestContext = object
deps_stub.api_key_auth = object
deps_stub.get_db = object
sys.modules.setdefault("demibot.http.deps", deps_stub)

import importlib.util
events_spec = importlib.util.spec_from_file_location(
    "demibot.http.routes.events", root / "demibot/http/routes/events.py"
)
events = importlib.util.module_from_spec(events_spec)
sys.modules[events_spec.name] = events
events_spec.loader.exec_module(events)

CreateEventBody = events.CreateEventBody
RepeatPatchBody = events.RepeatPatchBody


def test_create_event_body_normalizes_timezone():
    body = CreateEventBody(
        channelId="123",
        title="t",
        time="2024-05-01T12:30:00+02:00",
        description="d",
    )
    assert body.time == datetime(2024, 5, 1, 10, 30, 0, tzinfo=timezone.utc)


def test_create_event_body_invalid_time():
    with pytest.raises(HTTPException):
        CreateEventBody(
            channelId="123",
            title="t",
            time="not-a-time",
            description="d",
        )


def test_repeat_patch_body_timezone():
    body = RepeatPatchBody(time="2024-05-01T12:00:00-05:30")
    assert body.time == datetime(2024, 5, 1, 17, 30, 0, tzinfo=timezone.utc)


def test_repeat_patch_body_invalid():
    with pytest.raises(HTTPException):
        RepeatPatchBody(time="bad")
