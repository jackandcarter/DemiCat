
from __future__ import annotations
from datetime import datetime
from fastapi import APIRouter
from pydantic import BaseModel
from ._stores import USERS, OFFICER_MESSAGES
from ..schemas import ChatMessage, Mention

router = APIRouter(prefix="/api")

class PostBody(BaseModel):
    channelId: str
    content: str
    useCharacterName: bool | None = False

@router.get("/officer-messages/{channel_id}")
async def get_officer_messages(channel_id: str):
    out: list[ChatMessage] = []
    for m in OFFICER_MESSAGES.get(channel_id, []):
        out.append(ChatMessage(
            id=m.id,
            channelId=m.channel_id,
            authorName=m.author_name,
            content=m.content,
            mentions=[Mention(id=u.id, name=u.name) for u in (m.mentions or [])] or None
        ))
    return [o.model_dump() for o in out]

@router.post("/officer-messages")
async def post_officer_message(body: PostBody):
    mentions = []
    text = body.content
    for u in USERS:
        tag = f"@{u.name}"
        if tag in text:
            mentions.append(u)
            text = text.replace(tag, f"<@{u.id}>")

    msg = {
        "id": str(int(datetime.utcnow().timestamp()*1000)),
        "channel_id": body.channelId,
        "author_name": "Officer",
        "content": text,
        "mentions": mentions,
        "is_officer": True,
    }
    OFFICER_MESSAGES[body.channelId].append(type("Obj", (), msg))
    return {"ok": True}
