from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ._messages_common import _role_set
from ..discord_client import discord_client
from ...db.models import GuildConfig, Role
from ...discordbot.utils import is_premium_subscriber_role

router = APIRouter(prefix="/api")


@router.get("/guild-roles")
async def get_guild_roles(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    cfg = await db.scalar(
        select(GuildConfig).where(GuildConfig.guild_id == ctx.guild.id)
    )
    mention_role_ids = [
        rid
        for rid in (cfg.mention_role_ids.split(",") if cfg and cfg.mention_role_ids else [])
        if rid
    ]

    mention_ids: set[int] | None = None
    roles = _role_set(ctx)
    if "officer" not in roles:
        if mention_role_ids:
            mention_ids = {
                int(r)
                for r in mention_role_ids
                if r.isdigit()
            }
        else:
            return {
                "roles": [],
                "mention_role_ids": mention_role_ids,
            }

    stmt = select(Role).where(Role.guild_id == ctx.guild.id)
    if mention_ids is not None:
        stmt = stmt.where(Role.discord_role_id.in_(mention_ids))
    result = await db.execute(stmt)
    rows = result.scalars().all()
    if rows:
        return {
            "roles": [
            {
                "id": str(role.discord_role_id),
                "name": role.name,
                "position": role.position,
                "hoist": role.hoist,
                "tags": {
                    "premium_subscriber": bool(role.premium_subscriber),
                },
            }
            for role in rows
        ],
            "mention_role_ids": mention_role_ids,
        }

    if discord_client:
        guild = discord_client.get_guild(ctx.guild.discord_guild_id)
        if guild is not None:
            roles_out: list[dict[str, object]] = []
            existing = set()
            for r in guild.roles:
                if r.name == "@everyone":
                    continue
                if mention_ids is not None and r.id not in mention_ids:
                    continue
                is_premium_subscriber = is_premium_subscriber_role(r)
                role_data = {
                    "id": str(r.id),
                    "name": r.name,
                    "position": r.position,
                    "hoist": r.hoist,
                    "tags": {
                        "premium_subscriber": is_premium_subscriber,
                    },
                }
                roles_out.append(role_data)
                existing.add(r.id)
                await db.merge(
                    Role(
                        guild_id=ctx.guild.id,
                        discord_role_id=r.id,
                        name=r.name,
                        position=r.position,
                        hoist=r.hoist,
                        premium_subscriber=is_premium_subscriber,
                    )
                )
            await db.commit()
            return {
                "roles": roles_out,
                "mention_role_ids": mention_role_ids,
            }
    return {
        "roles": [],
        "mention_role_ids": mention_role_ids,
    }

