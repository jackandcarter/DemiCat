from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..discord_client import discord_client
from ...db.models import Role, GuildConfig

router = APIRouter(prefix="/api")


@router.get("/guild-roles")
async def get_guild_roles(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    mention_ids: set[int] | None = None
    if "officer" not in ctx.roles:
        cfg = await db.scalar(
            select(GuildConfig).where(GuildConfig.guild_id == ctx.guild.id)
        )
        if cfg and cfg.mention_role_ids:
            mention_ids = {
                int(r) for r in cfg.mention_role_ids.split(",") if r
            }
        else:
            return []

    stmt = select(Role.discord_role_id, Role.name).where(
        Role.guild_id == ctx.guild.id
    )
    if mention_ids is not None:
        stmt = stmt.where(Role.discord_role_id.in_(mention_ids))
    result = await db.execute(stmt)
    rows = result.all()
    if rows:
        return [{"id": str(rid), "name": name} for rid, name in rows]

    if discord_client:
        guild = discord_client.get_guild(ctx.guild.discord_guild_id)
        if guild is not None:
            roles_out: list[dict[str, str]] = []
            existing = set()
            for r in guild.roles:
                if r.name == "@everyone":
                    continue
                if mention_ids is not None and r.id not in mention_ids:
                    continue
                roles_out.append({"id": str(r.id), "name": r.name})
                existing.add(r.id)
                await db.merge(
                    Role(
                        guild_id=ctx.guild.id,
                        discord_role_id=r.id,
                        name=r.name,
                    )
                )
            await db.commit()
            return roles_out
    return []

