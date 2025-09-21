"""Utilities for lazily importing the Discord bot factory."""

from __future__ import annotations

from importlib import import_module
from typing import Any, TYPE_CHECKING

if TYPE_CHECKING:  # pragma: no cover - type checking only
    from .bot import create_bot as _create_bot


def create_bot(*args: Any, **kwargs: Any):
    """Import and delegate to :func:`demibot.discordbot.bot.create_bot`.

    Tests often stub the :mod:`discord` package with partial implementations.
    Importing the bot module eagerly would pull in the heavy discord.py
    dependency and fail under these lightweight stubs.  Delaying the import
    keeps the package importable even when optional dependencies are missing.
    """

    module = import_module("demibot.discordbot.bot")
    create = getattr(module, "create_bot")
    return create(*args, **kwargs)


__all__ = ["create_bot"]
