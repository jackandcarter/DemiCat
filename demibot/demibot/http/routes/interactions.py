
from __future__ import annotations
from typing import Optional, Dict, List
from fastapi import APIRouter
from pydantic import BaseModel
from ._stores import EMBEDS, Embed
from ..ws import manager
import json

router = APIRouter(prefix="/api")

# Tracks per-embed attendance: { embed_id: { "yes": set(user_ids), ... } }
ATTENDANCE: Dict[str, Dict[str, set[str]]] = {}

class InteractionBody(BaseModel):
    MessageId: str
    ChannelId: int | None = None
    CustomId: str

def summarize(att: Dict[str, set[str]]) -> List[dict]:
    # Turn attendance into embed fields
    fields = []
    for key in ["yes","maybe","no"]:
        people = sorted(list(att.get(key, set())))
        fields.append({"name": key.capitalize(), "value": ", ".join(people) if people else "—"})
    return fields

@router.post("/interactions")
async def post_interaction(body: InteractionBody):
    # For now we don't know the player's identity from plugin call; use placeholder "Player"
    user = "Player"
    choice = body.CustomId.split(":",1)[1] if ":" in body.CustomId else body.CustomId
    att = ATTENDANCE.setdefault(body.MessageId, {"yes": set(), "maybe": set(), "no": set()})
    # Toggle: if already selected, remove; else add
    if user in att.get(choice, set()):
        att[choice].remove(user)
    else:
        att[choice].add(user)

    # Update embed fields
    e = EMBEDS.get(body.MessageId)
    if e:
        payload = dict(e.payload)
        payload["fields"] = summarize(att)
        e.payload = payload
        await manager.broadcast_text(json.dumps(payload))

    return {"ok": True}
