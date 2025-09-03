
from __future__ import annotations

from typing import List, Optional
from pydantic import BaseModel, Field
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
    custom_id: Optional[str] = Field(default=None, alias="customId")
    emoji: Optional[str] = None
    style: Optional[ButtonStyle] = None
    max_signups: Optional[int] = Field(default=None, alias="maxSignups")


class EmbedAuthorDto(BaseModel):
    name: Optional[str] = None
    url: Optional[str] = None
    icon_url: Optional[str] = Field(default=None, alias="iconUrl")

class EmbedDto(BaseModel):
    id: str
    timestamp: Optional[datetime] = None
    color: Optional[int] = None
    author_name: Optional[str] = Field(default=None, alias="authorName")
    author_icon_url: Optional[str] = Field(default=None, alias="authorIconUrl")
    authors: List[EmbedAuthorDto] | None = None
    title: Optional[str] = None
    description: Optional[str] = None
    url: Optional[str] = None
    fields: List[EmbedFieldDto] | None = None
    thumbnail_url: Optional[str] = Field(default=None, alias="thumbnailUrl")
    image_url: Optional[str] = Field(default=None, alias="imageUrl")
    provider_name: Optional[str] = Field(default=None, alias="providerName")
    provider_url: Optional[str] = Field(default=None, alias="providerUrl")
    footer_text: Optional[str] = Field(default=None, alias="footerText")
    footer_icon_url: Optional[str] = Field(default=None, alias="footerIconUrl")
    video_url: Optional[str] = Field(default=None, alias="videoUrl")
    video_width: Optional[int] = Field(default=None, alias="videoWidth")
    video_height: Optional[int] = Field(default=None, alias="videoHeight")
    buttons: List[EmbedButtonDto] | None = None
    channel_id: Optional[int] = Field(default=None, alias="channelId")
    mentions: List[int] | None = None

# ---- Chat ----

class Mention(BaseModel):
    id: str
    name: str


class AttachmentDto(BaseModel):
    url: str
    filename: Optional[str] = None
    content_type: Optional[str] = Field(default=None, alias="contentType")


class ButtonComponentDto(BaseModel):
    label: str
    custom_id: Optional[str] = Field(default=None, alias="customId")
    url: Optional[str] = None
    style: ButtonStyle
    emoji: Optional[str] = None


class MessageAuthor(BaseModel):
    id: str
    name: str
    avatar_url: Optional[str] = Field(default=None, alias="avatarUrl")
    use_character_name: bool | None = Field(default=False, alias="useCharacterName")


class ReactionDto(BaseModel):
    emoji: str
    emoji_id: str | None = Field(default=None, alias="emojiId")
    is_animated: bool = Field(alias="isAnimated")
    count: int
    me: bool


class MessageReferenceDto(BaseModel):
    message_id: str = Field(alias="messageId")
    channel_id: str = Field(alias="channelId")


class ChatMessage(BaseModel):
    id: str
    channel_id: str = Field(alias="channelId")
    author_name: str = Field(alias="authorName")
    author_avatar_url: Optional[str] = Field(default=None, alias="authorAvatarUrl")
    timestamp: Optional[datetime] = None
    content: str
    attachments: List[AttachmentDto] | None = None
    mentions: List[Mention] | None = None
    author: MessageAuthor | None = None
    embeds: List[EmbedDto] | None = None
    reference: MessageReferenceDto | None = None
    components: List[ButtonComponentDto] | None = None
    reactions: List[ReactionDto] | None = None
    edited_timestamp: Optional[datetime] = Field(default=None, alias="editedTimestamp")
    use_character_name: bool | None = Field(default=False, alias="useCharacterName")


# ---- Presence ----


class PresenceDto(BaseModel):
    id: str
    name: str
    status: str
    avatar_url: str | None = Field(default=None, alias="avatarUrl")
    roles: List[str] = Field(default_factory=list)
