from __future__ import annotations

from discord.ext import commands


class _DiscordClientProxy:
    """Proxy object that always reflects the current Discord client.

    HTTP route modules often import ``discord_client`` directly, meaning they
    receive the value that existed at import time.  Reassigning the module level
    ``discord_client`` would therefore leave those imports pointing at the old
    object (usually ``None``) even after the bot is created.  The proxy keeps a
    mutable reference to the underlying client so that existing imports stay in
    sync when :func:`set_discord_client` updates the client.
    """

    __slots__ = ("_client",)

    def __init__(self) -> None:
        self._client: commands.Bot | None = None

    def set(self, client: commands.Bot | None) -> None:
        self._client = client

    def get(self) -> commands.Bot | None:
        return self._client

    def __bool__(self) -> bool:
        return self._client is not None

    def __getattr__(self, name: str):  # pragma: no cover - passthrough
        client = self._client
        if client is None:
            raise AttributeError(name)
        return getattr(client, name)


discord_client = _DiscordClientProxy()


def set_discord_client(client: commands.Bot | None) -> None:
    """Register the Discord client used for sending messages.

    The HTTP routes rely on this client to forward messages to Discord
    channels.  The function can be called during application startup to supply
    the running :class:`commands.Bot` instance.
    """

    discord_client.set(client)


def get_discord_client() -> commands.Bot | None:
    """Return the currently registered Discord client, if any."""

    return discord_client.get()


def is_discord_client_ready(client: commands.Bot | _DiscordClientProxy | None = None) -> bool:
    """Return ``True`` when the Discord client is ready for API access.

    The helper performs defensive checks around the lifecycle helpers exposed
    by :mod:`discord.py`, ensuring the client both exists and has completed its
    internal startup sequence.  Any unexpected exceptions are treated as a
    disconnected state so that HTTP routes can fail fast while the bot is still
    syncing with Discord.
    """

    if isinstance(client, _DiscordClientProxy):
        client = client.get()

    if client is None:
        client = discord_client.get()
    if client is None:
        return False

    try:
        is_closed = getattr(client, "is_closed", None)
        if callable(is_closed):
            if is_closed():
                return False
        elif is_closed:
            return False
    except Exception:
        return False

    try:
        ready_attr = getattr(client, "is_ready", None)
    except Exception:
        ready_attr = None

    if ready_attr is None:
        return False

    try:
        ready_value = ready_attr() if callable(ready_attr) else ready_attr
    except Exception:
        return False

    if isinstance(ready_value, bool):
        return ready_value

    try:
        is_set = getattr(ready_value, "is_set", None)
        if callable(is_set):
            return bool(is_set())
    except Exception:
        return False

    return bool(ready_value)

