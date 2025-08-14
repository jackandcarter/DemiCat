
from __future__ import annotations
from datetime import datetime
from typing import Optional, List
from fastapi import APIRouter
from pydantic import BaseModel
from ._stores import EMBEDS, Embed
from ..schemas import EmbedDto, EmbedFieldDto, EmbedButtonDto
from ..ws import manager
import json, uuid

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
    attendance: List[str] | None = None

@router.post("/events")
async def create_event(body: CreateEventBody):
    eid = str(uuid.uuid4())
    buttons = []
    for tag in (body.attendance or ["yes","maybe","no"]):
        buttons.append(EmbedButtonDto(label=tag.capitalize(), customId=f"rsvp:{tag}"))
    dto = EmbedDto(
        id=eid,
        timestamp=datetime.fromisoformat(body.time.replace("Z","+00:00")) if body.time else datetime.utcnow(),
        color=body.color,
        authorName=None,
        authorIconUrl=None,
        title=body.title,
        description=body.description,
        fields=[EmbedFieldDto(name=f.name, value=f.value, inline=f.inline or False) for f in (body.fields or [])],
        thumbnailUrl=body.thumbnailUrl,
        imageUrl=body.imageUrl,
        buttons=buttons,
        channelId=int(body.channelId) if body.channelId.isdigit() else None,
        mentions=None,
    )
    EMBEDS[eid] = Embed(id=eid, payload=dto.model_dump())
    await manager.broadcast_text(json.dumps(dto.model_dump()))
    return {"ok": True, "id": eid}
