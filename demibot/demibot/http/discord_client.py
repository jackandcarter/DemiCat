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

