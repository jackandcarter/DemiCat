from __future__ import annotations

from discord.ext import commands


discord_client: commands.Bot | None = None


def set_discord_client(client: commands.Bot) -> None:
    """Register the Discord client used for sending messages.

    The HTTP routes rely on this client to forward messages to Discord
    channels.  The function can be called during application startup to supply
    the running :class:`commands.Bot` instance.
    """

    global discord_client
    discord_client = client


def is_discord_client_ready(client: commands.Bot | None = None) -> bool:
    """Return ``True`` when the Discord client is ready for API access.

    The helper performs defensive checks around the lifecycle helpers exposed
    by :mod:`discord.py`, ensuring the client both exists and has completed its
    internal startup sequence.  Any unexpected exceptions are treated as a
    disconnected state so that HTTP routes can fail fast while the bot is still
    syncing with Discord.
    """

    client = client or discord_client
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
        if callable(ready_attr):
            return bool(ready_attr())
        if ready_attr is not None:
            return bool(ready_attr)
    except Exception:
        return False

    return False

