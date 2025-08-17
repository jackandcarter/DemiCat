from __future__ import annotations

import json
from datetime import datetime
from typing import List, Optional

from fastapi import APIRouter, Depends
from sqlalchemy import select
from pydantic import BaseModel
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import EmbedDto, EmbedFieldDto, EmbedButtonDto
from ..ws import manager
from ...db.models import Embed, GuildChannel

router = APIRouter(prefix="/api")


class FieldBody(BaseModel):
    name: str
    value: str
    inline: bool | None = None


class CreateEventBody(BaseModel):
    channelId: str
    title: str
    time: str
    description: str
    url: Optional[str] = None
    imageUrl: Optional[str] = None
    thumbnailUrl: Optional[str] = None
    color: Optional[int] = None
    fields: List[FieldBody] | None = None
    buttons: List[EmbedButtonDto] | None = None
    attendance: List[str] | None = None


@router.post("/events")
async def create_event(
    body: CreateEventBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    eid = str(int(datetime.utcnow().timestamp() * 1000))
    buttons = body.buttons or []
    if not buttons:
        for tag in (body.attendance or ["yes", "maybe", "no"]):
            buttons.append(EmbedButtonDto(label=tag.capitalize(), customId=f"rsvp:{tag}"))
    dto = EmbedDto(
        id=eid,
        timestamp=datetime.fromisoformat(body.time.replace("Z", "+00:00"))
        if body.time
        else datetime.utcnow(),
        color=body.color,
        authorName=None,
        authorIconUrl=None,
        title=body.title,
        description=body.description,
        fields=[
            EmbedFieldDto(name=f.name, value=f.value, inline=f.inline or False)
            for f in (body.fields or [])
        ],
        thumbnailUrl=body.thumbnailUrl,
        imageUrl=body.imageUrl,
        buttons=buttons,
        channelId=int(body.channelId) if body.channelId.isdigit() else None,
        mentions=None,
    )
    channel_id = int(body.channelId)
    db.add(
        Embed(
            discord_message_id=int(eid),
            channel_id=channel_id,
            guild_id=ctx.guild.id,
            payload_json=json.dumps(dto.model_dump()),
            source="demibot",
        )
    )
    await db.commit()
    kind = (
        await db.execute(
            select(GuildChannel.kind).where(
                GuildChannel.guild_id == ctx.guild.id,
                GuildChannel.channel_id == channel_id,
            )
        )
    ).scalar_one_or_none()
    await manager.broadcast_text(
        json.dumps(dto.model_dump()), ctx.guild.id, kind == "officer_chat"
    )
    return {"ok": True, "id": eid}
