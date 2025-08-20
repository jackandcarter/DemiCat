from __future__ import annotations

import asyncio
import logging

from sqlalchemy import select, update
from sqlalchemy.ext.asyncio import AsyncSession

from .db.models import GuildChannel
from .db.session import get_session
from .http.discord_client import discord_client


async def ensure_channel_name(
    db: AsyncSession,
    guild_id: int,
    channel_id: int,
    kind: str,
    current_name: str | None = None,
) -> str | None:
    """Ensure the channel's name is stored in the database.

    If ``current_name`` is falsy, the Discord API is queried for the channel's
    name and the database is updated.  The resolved name is returned (or
    ``None`` if it could not be resolved).
    """

    if current_name or not discord_client:
        return current_name
    channel = discord_client.get_channel(channel_id)
    if channel is None:
        try:
            channel = await discord_client.fetch_channel(channel_id)  # type: ignore[attr-defined]
        except Exception:  # pragma: no cover - network errors
            logging.warning("Failed to fetch channel %s", channel_id)
            return str(channel_id)
    if channel is None:
        logging.warning("Channel %s not found", channel_id)
        return str(channel_id)
    name = channel.name
    await db.execute(
        update(GuildChannel)
        .where(
            GuildChannel.guild_id == guild_id,
            GuildChannel.channel_id == channel_id,
            GuildChannel.kind == kind,
        )
        .values(name=name)
    )
    return name


async def resync_channel_names_once() -> None:
    """Refresh channel names for all guilds once."""

    async for db in get_session():
        result = await db.execute(
            select(
                GuildChannel.guild_id,
                GuildChannel.channel_id,
                GuildChannel.kind,
                GuildChannel.name,
            )
        )
        updated = False
        for guild_id, channel_id, kind, name in result.all():
            new_name = await ensure_channel_name(db, guild_id, channel_id, kind, name)
            if new_name and new_name != name:
                updated = True
        if updated:
            await db.commit()
        break


SYNC_INTERVAL = 3600


async def channel_name_resync() -> None:
    """Background task to periodically resync channel names."""

    while True:
        try:
            await resync_channel_names_once()
        except Exception:  # pragma: no cover - best effort
            logging.exception("Channel name resync failed")
        await asyncio.sleep(SYNC_INTERVAL)
