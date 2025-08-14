
from __future__ import annotations
from datetime import datetime
from fastapi import APIRouter
from pydantic import BaseModel
from ._stores import USERS, MESSAGES
from ..schemas import ChatMessage, Mention

router = APIRouter(prefix="/api")

class PostBody(BaseModel):
    channelId: str
    content: str
    useCharacterName: bool | None = False

@router.get("/messages/{channel_id}")
async def get_messages(channel_id: str):
    out: list[ChatMessage] = []
    for m in MESSAGES.get(channel_id, []):
        out.append(ChatMessage(
            id=m.id,
            channelId=m.channel_id,
            authorName=m.author_name,
            content=m.content,
            mentions=[Mention(id=u.id, name=u.name) for u in (m.mentions or [])] or None
        ))
    return [o.model_dump() for o in out]

@router.post("/messages")
async def post_message(body: PostBody):
    # naive mention parsing: convert @Name to <@id> for storage and provide mentions list
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
        "author_name": "Player",
        "content": text,
        "mentions": mentions,
        "is_officer": False,
    }
    MESSAGES[body.channelId].append(type("Obj", (), msg))
    return {"ok": True}
