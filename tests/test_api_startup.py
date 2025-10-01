from __future__ import annotations

import asyncio
from unittest.mock import AsyncMock

from demibot.http import api as api_module
from demibot.http.routes import syncshell as syncshell_module


class _DummySessionContext:
    async def __aenter__(self) -> object:  # pragma: no cover - trivial
        return object()

    async def __aexit__(self, exc_type, exc, tb) -> None:  # pragma: no cover - trivial
        return None


class _FakeLogger:
    def __init__(self) -> None:
        self.warnings: list[tuple[str, dict[str, object]]] = []

    def warning(self, event: str, **kwargs: object) -> None:
        self.warnings.append((event, kwargs))


def test_startup_logs_warning_when_transfer_budget_load_fails(monkeypatch):
    fake_logger = _FakeLogger()
    monkeypatch.setattr(api_module, "logger", fake_logger)

    async def failing_loader(db) -> None:
        raise RuntimeError("boom")

    monkeypatch.setattr(syncshell_module, "load_transfer_budgets", failing_loader)

    reset_mock = AsyncMock()
    monkeypatch.setattr(syncshell_module._transfer_budget_store, "reset", reset_mock)

    monkeypatch.setattr(api_module, "get_session", lambda: _DummySessionContext())
    monkeypatch.setattr(api_module.pkgutil, "iter_modules", lambda *args, **kwargs: [])

    app = api_module.create_app()

    asyncio.run(app.router.startup())

    assert fake_logger.warnings
    event, data = fake_logger.warnings[0]
    assert event == "syncshell.transfer_budgets_load_failed"
    assert "error" in data
    assert "boom" in str(data["error"])

    reset_mock.assert_awaited_once()

    asyncio.run(app.router.shutdown())
