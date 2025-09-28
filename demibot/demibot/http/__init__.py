from __future__ import annotations

from importlib import import_module
from typing import TYPE_CHECKING, Any

__all__ = ["create_app"]


def __getattr__(name: str) -> Any:
    if name == "create_app":
        return import_module(".api", __name__).create_app
    raise AttributeError(name)


if TYPE_CHECKING:  # pragma: no cover - for static type checkers
    from .api import create_app as create_app
