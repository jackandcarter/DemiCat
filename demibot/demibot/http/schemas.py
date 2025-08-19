
from __future__ import annotations

from typing import List, Optional
from pydantic import BaseModel
from datetime import datetime
from enum import IntEnum

# ---- Embeds ----

class EmbedFieldDto(BaseModel):
    name: str
    value: str
    inline: bool | None = None

class ButtonStyle(IntEnum):
    primary = 1
    secondary = 2
    success = 3
    danger = 4
    link = 5


class EmbedButtonDto(BaseModel):
    label: str
    url: Optional[str] = None
    customId: Optional[str] = None
    emoji: Optional[str] = None
    style: Optional[ButtonStyle] = None
    maxSignups: Optional[int] = None

class EmbedDto(BaseModel):
    id: str
    timestamp: Optional[datetime] = None
    color: Optional[int] = None
    authorName: Optional[str] = None
    authorIconUrl: Optional[str] = None
    title: Optional[str] = None
    description: Optional[str] = None
    url: Optional[str] = None
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

# ---- Presence ----


class PresenceDto(BaseModel):
    id: str
    name: str
    status: str
