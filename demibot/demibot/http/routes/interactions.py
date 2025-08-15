from __future__ import annotations

import json
from typing import Dict, List

from fastapi import APIRouter, Depends
from pydantic import BaseModel
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..ws import manager
from ...db.models import Attendance, Embed, RSVP, User

router = APIRouter(prefix="/api")


class InteractionBody(BaseModel):
    MessageId: str
    ChannelId: int | None = None
    CustomId: str


def summarize(att: Dict[str, List[str]]) -> List[dict]:
    fields = []
    for key in ["yes", "maybe", "no"]:
        people = att.get(key, [])
        fields.append({"name": key.capitalize(), "value": ", ".join(people) if people else "—"})
    return fields


@router.post("/interactions")
async def post_interaction(
    body: InteractionBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    choice = body.CustomId.split(":", 1)[1] if ":" in body.CustomId else body.CustomId
    message_id = int(body.MessageId)

    stmt = select(Attendance).where(
        Attendance.discord_message_id == message_id,
        Attendance.user_id == ctx.user.id,
    )
    row = (await db.execute(stmt)).scalar_one_or_none()
    if row and row.choice.value == choice:
        await db.delete(row)
    else:
        if row:
            row.choice = RSVP(choice)
        else:
            db.add(
                Attendance(
                    discord_message_id=message_id,
                    user_id=ctx.user.id,
                    choice=RSVP(choice),
                )
            )
    await db.commit()

    # recompute attendance summary
    stmt = (
        select(Attendance.choice, User.global_name)
        .join(User, Attendance.user_id == User.id)
        .where(Attendance.discord_message_id == message_id)
    )
    rows = await db.execute(stmt)
    summary: Dict[str, List[str]] = {"yes": [], "maybe": [], "no": []}
    for choice, name in rows.all():
        summary[choice.value].append(name or str(ctx.user.discord_user_id))

    embed = (
        await db.execute(select(Embed).where(Embed.discord_message_id == message_id))
    ).scalar_one_or_none()
    if embed:
        payload = json.loads(embed.payload_json)
        payload["fields"] = summarize(summary)
        embed.payload_json = json.dumps(payload)
        await db.commit()
        await manager.broadcast_text(json.dumps(payload))

    return {"ok": True}
