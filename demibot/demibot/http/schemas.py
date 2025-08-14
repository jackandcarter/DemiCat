
from __future__ import annotations

from typing import List, Optional
from pydantic import BaseModel
from datetime import datetime

# ---- Embeds ----

class EmbedFieldDto(BaseModel):
    name: str
    value: str
    inline: bool | None = None

class EmbedButtonDto(BaseModel):
    label: str
    url: Optional[str] = None
    customId: Optional[str] = None

class EmbedDto(BaseModel):
    id: str
    timestamp: Optional[datetime] = None
    color: Optional[int] = None
    authorName: Optional[str] = None
    authorIconUrl: Optional[str] = None
    title: Optional[str] = None
    description: Optional[str] = None
    fields: List[EmbedFieldDto] | None = None
    thumbnailUrl: Optional[str] = None
    imageUrl: Optional[str] = None
    buttons: List[EmbedButtonDto] | None = None
    channelId: Optional[int] = None
    mentions: List[int] | None = None

# ---- Chat ----

class Mention(BaseModel):
    id: str
    name: str

class ChatMessage(BaseModel):
    id: str
    channelId: str
    authorName: str
    content: str
    mentions: List[Mention] | None = None
