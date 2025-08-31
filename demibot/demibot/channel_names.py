from __future__ import annotations

import asyncio
import logging

import aiohttp
from sqlalchemy import select, update
from sqlalchemy.ext.asyncio import AsyncSession

from .db.models import GuildChannel
from .db.session import get_session
from .http.discord_client import discord_client
from .http.ws import manager
from .config import load_config


async def ensure_channel_name(
    db: AsyncSession,
    guild_id: int,
    channel_id: int,
    kind: str,
    current_name: str | None = None,
) -> str | None:
    """Ensure the channel's name is stored in the database.

    If ``current_name`` is falsy or composed solely of digits, the Discord API
    is queried for the channel's name and the database is updated.  The
    resolved name is returned (or ``None`` if it could not be resolved).
    """

    if current_name and not current_name.isdigit():
        return current_name

    # Try to resolve via Discord gateway client if available, otherwise fall
    # back to a direct REST API call using the configured bot token.
    if discord_client:
        channel = discord_client.get_channel(channel_id)
        if channel is None:
            try:
                channel = await discord_client.fetch_channel(channel_id)  # type: ignore[attr-defined]
            except Exception as exc:  # pragma: no cover - network errors
                logging.warning(
                    "Failed to fetch channel %s in guild %s: %s",
                    channel_id,
                    guild_id,
                    exc,
                )
                return None
        if channel is None:
            logging.warning("Channel %s not found in guild %s", channel_id, guild_id)
            return None
        name = channel.name
    else:
        cfg = load_config()
        if not cfg.discord_token:
            logging.warning(
                "Discord token missing; cannot resolve channel %s", channel_id
            )
            return None
        url = f"https://discord.com/api/v10/channels/{channel_id}"
        headers = {"Authorization": f"Bot {cfg.discord_token}"}
        try:
            async with aiohttp.ClientSession() as session:
                async with session.get(url, headers=headers) as resp:
                    if resp.status != 200:
                        logging.warning(
                            "Failed to fetch channel %s in guild %s: HTTP %s",
                            channel_id,
                            guild_id,
                            resp.status,
                        )
                        return None
                    data = await resp.json()
        except Exception as exc:  # pragma: no cover - network errors
            logging.warning(
                "Failed to fetch channel %s in guild %s: %s",
                channel_id,
                guild_id,
                exc,
            )
            return None
        name = data.get("name")
        if not name:
            logging.warning(
                "Channel %s returned no name in guild %s", channel_id, guild_id
            )
            return None
    await db.execute(
        update(GuildChannel)
        .where(
            GuildChannel.guild_id == guild_id,
            GuildChannel.channel_id == channel_id,
            GuildChannel.kind == kind,
        )
        .values(name=name)
    )
    if name != current_name:
        await manager.broadcast_text("update", guild_id, path="/ws/channels")
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
        updated_guilds: set[int] = set()
        for guild_id, channel_id, kind, name in result.all():
            new_name = await ensure_channel_name(db, guild_id, channel_id, kind, name)
            if new_name is not None and new_name != name:
                updated_guilds.add(guild_id)
        if updated_guilds:
            await db.commit()
            for guild_id in updated_guilds:
                await manager.broadcast_text("update", guild_id, path="/ws/channels")
        break


async def retry_null_channel_names(max_attempts: int = 3) -> None:
    """Retry resolving channel names that are still ``NULL``.

    Channels whose names cannot be resolved after ``max_attempts`` attempts are
    logged with their guild and channel IDs for further investigation.
    """

    async for db in get_session():
        unresolved: list[tuple[int, int]] = []
        for attempt in range(max_attempts):
            result = await db.execute(
                select(
                    GuildChannel.guild_id,
                    GuildChannel.channel_id,
                    GuildChannel.kind,
                ).where(GuildChannel.name.is_(None))
            )
            rows = result.all()
            if not rows:
                break
            unresolved = []
            updated_guilds: set[int] = set()
            for guild_id, channel_id, kind in rows:
                name = await ensure_channel_name(db, guild_id, channel_id, kind, None)
                if name is None:
                    unresolved.append((guild_id, channel_id))
                else:
                    updated_guilds.add(guild_id)
            if updated_guilds:
                await db.commit()
                for guild_id in updated_guilds:
                    await manager.broadcast_text(
                        "update", guild_id, path="/ws/channels"
                    )
            if not unresolved:
                break
        for guild_id, channel_id in unresolved:
            logging.warning(
                "Channel name missing for %s in guild %s after %s attempts",
                channel_id,
                guild_id,
                max_attempts,
            )
        break


SYNC_INTERVAL = 3600


async def channel_name_resync() -> None:
    """Background task to periodically resync channel names."""

    while True:
        try:
            await resync_channel_names_once()
            await retry_null_channel_names()
        except Exception:  # pragma: no cover - best effort
            logging.exception("Channel name resync failed")
        await asyncio.sleep(SYNC_INTERVAL)
