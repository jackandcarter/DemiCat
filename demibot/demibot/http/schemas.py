
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


class EmbedAuthorDto(BaseModel):
    name: Optional[str] = None
    url: Optional[str] = None
    iconUrl: Optional[str] = None

class EmbedDto(BaseModel):
    id: str
    timestamp: Optional[datetime] = None
    color: Optional[int] = None
    authorName: Optional[str] = None
    authorIconUrl: Optional[str] = None
    authors: List[EmbedAuthorDto] | None = None
    title: Optional[str] = None
    description: Optional[str] = None
    url: Optional[str] = None
    fields: List[EmbedFieldDto] | None = None
    thumbnailUrl: Optional[str] = None
    imageUrl: Optional[str] = None
    providerName: Optional[str] = None
    providerUrl: Optional[str] = None
    footerText: Optional[str] = None
    footerIconUrl: Optional[str] = None
    videoUrl: Optional[str] = None
    videoWidth: Optional[int] = None
    videoHeight: Optional[int] = None
    buttons: List[EmbedButtonDto] | None = None
    channelId: Optional[int] = None
    mentions: List[int] | None = None

# ---- Chat ----

class Mention(BaseModel):
    id: str
    name: str


class AttachmentDto(BaseModel):
    url: str
    filename: Optional[str] = None
    contentType: Optional[str] = None


class MessageAuthor(BaseModel):
    id: str
    name: str
    avatarUrl: Optional[str] = None
    useCharacterName: bool | None = False


class ChatMessage(BaseModel):
    id: str
    channelId: str
    authorName: str
    authorAvatarUrl: Optional[str] = None
    timestamp: Optional[datetime] = None
    content: str
    attachments: List[AttachmentDto] | None = None
    mentions: List[Mention] | None = None
    author: MessageAuthor | None = None
    embeds: List[dict] | None = None
    reference: dict | None = None
    components: List[dict] | None = None
    editedTimestamp: Optional[datetime] = None
    useCharacterName: bool | None = False

# ---- Presence ----


class PresenceDto(BaseModel):
    id: str
    name: str
    status: str
