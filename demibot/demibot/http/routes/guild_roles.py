from __future__ import annotations

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..discord_client import discord_client
from ...db.models import Role

router = APIRouter(prefix="/api")


@router.get("/guild-roles")
async def get_guild_roles(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    result = await db.execute(
        select(Role.discord_role_id, Role.name).where(Role.guild_id == ctx.guild.id)
    )
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

