import asyncio
import sys
import types
from pathlib import Path

import pytest

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

# Stub package structure to avoid heavy dependencies when importing.
demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

discordbot_pkg = types.ModuleType("demibot.discordbot")
discordbot_pkg.__path__ = [str(root / "demibot/discordbot")]
sys.modules.setdefault("demibot.discordbot", discordbot_pkg)

from demibot.discordbot.utils import api_call_with_retries


class _FakeError(Exception):
    def __init__(self, status: int | None = None):
        super().__init__("fail")
        self.status = status


async def _run_success() -> None:
    calls = {"n": 0}

    async def fn() -> str:
        calls["n"] += 1
        if calls["n"] < 3:
            raise _FakeError(500)
        return "ok"

    result = await api_call_with_retries(fn, retries=3, base_delay=0)
    assert result == "ok"
    assert calls["n"] == 3


def test_retry_succeeds() -> None:
    asyncio.run(_run_success())


async def _run_failure() -> None:
    calls = {"n": 0}

    async def fn() -> None:
        calls["n"] += 1
        raise _FakeError(500)

    with pytest.raises(_FakeError):
        await api_call_with_retries(fn, retries=2, base_delay=0)
    assert calls["n"] == 2


def test_retry_fails() -> None:
    asyncio.run(_run_failure())
