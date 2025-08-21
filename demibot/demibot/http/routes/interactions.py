from __future__ import annotations

import json
from typing import Dict, List

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse
from pydantic import BaseModel
from sqlalchemy import select, func
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..ws import manager
from ...db.models import Attendance, Embed, EventButton, GuildChannel, User

router = APIRouter(prefix="/api")


class InteractionBody(BaseModel):
    MessageId: str
    ChannelId: int | None = None
    CustomId: str


def summarize(att: Dict[str, List[str]], labels: Dict[str, str], order: List[str]) -> List[dict]:
    fields = []
    for key in order:
        people = att.get(key, [])
        label = labels.get(key, key.capitalize())
        fields.append({"name": label, "value": ", ".join(people) if people else "—"})
    return fields


@router.post("/interactions")
async def post_interaction(
    body: InteractionBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    choice = body.CustomId.split(":", 1)[1] if ":" in body.CustomId else body.CustomId
    message_id = int(body.MessageId)
    embed = (
        await db.execute(select(Embed).where(Embed.discord_message_id == message_id))
    ).scalar_one_or_none()
    labels: Dict[str, str] = {}
    order: List[str] = []
    limits: Dict[str, int] = {}

    rows = await db.execute(
        select(EventButton).where(EventButton.message_id == message_id)
    )
    for b in rows.scalars():
        order.append(b.tag)
        labels[b.tag] = b.label
        if b.max_signups is not None:
            limits[b.tag] = b.max_signups

    if embed:
        payload = json.loads(embed.payload_json)

    stmt = select(Attendance).where(
        Attendance.discord_message_id == message_id,
        Attendance.user_id == ctx.user.id,
    )
    row = (await db.execute(stmt)).scalar_one_or_none()
    if row and row.choice == choice:
        await db.delete(row)
    else:
        if row and row.choice != choice:
            limit = limits.get(choice)
            if limit is not None:
                count_stmt = select(func.count()).where(
                    Attendance.discord_message_id == message_id,
                    Attendance.choice == choice,
                )
                count = (await db.execute(count_stmt)).scalar_one()
                if count >= limit:
                    return JSONResponse({"error": "Full"}, status_code=400)
            row.choice = choice
        elif row is None:
            limit = limits.get(choice)
            if limit is not None:
                count_stmt = select(func.count()).where(
                    Attendance.discord_message_id == message_id,
                    Attendance.choice == choice,
                )
                count = (await db.execute(count_stmt)).scalar_one()
                if count >= limit:
                    return JSONResponse({"error": "Full"}, status_code=400)
            db.add(
                Attendance(
                    discord_message_id=message_id,
                    user_id=ctx.user.id,
                    choice=choice,
                )
            )
    await db.commit()

    # recompute attendance summary
    stmt = (
        select(
            Attendance.choice,
            User.global_name,
            User.discriminator,
            User.discord_user_id,
        )
        .join(User, Attendance.user_id == User.id)
        .where(Attendance.discord_message_id == message_id)
    )
    rows = await db.execute(stmt)
    summary: Dict[str, List[str]] = {}
    for c, name, discrim, uid in rows.all():
        display = name or discrim or str(uid)
        summary.setdefault(c, []).append(display)

    if embed:
        payload["fields"] = summarize(summary, labels, order or list(summary.keys()))
        embed.payload_json = json.dumps(payload)
        await db.commit()
        kind = (
            await db.execute(
                select(GuildChannel.kind).where(
                    GuildChannel.guild_id == embed.guild_id,
                    GuildChannel.channel_id == embed.channel_id,
                )
            )
        ).scalar_one_or_none()
        await manager.broadcast_text(
            json.dumps(payload),
            embed.guild_id,
            officer_only=kind == "officer_chat",
            path="/ws/embeds",
        )

    return {"ok": True}
