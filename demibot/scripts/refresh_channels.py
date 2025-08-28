from __future__ import annotations

import asyncio

from sqlalchemy import select, update

from demibot.config import ensure_config
from demibot.db.models import GuildChannel
from demibot.db.session import get_session, init_db


async def _refresh() -> None:
    cfg = await ensure_config()
    await init_db(cfg.database.url)
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
            if name and name.isdigit():
                await db.execute(
                    update(GuildChannel)
                    .where(
                        GuildChannel.guild_id == guild_id,
                        GuildChannel.channel_id == channel_id,
                        GuildChannel.kind == kind,
                    )
                    .values(name=None)
                )
                updated = True
        if updated:
            await db.commit()
        break


if __name__ == "__main__":
    asyncio.run(_refresh())
