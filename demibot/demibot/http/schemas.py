
from __future__ import annotations

from typing import List, Optional
from pydantic import BaseModel, Field, ConfigDict
from datetime import datetime
from enum import IntEnum

# ---- Embeds ----


def to_camel(string: str) -> str:
    parts = string.split("_")
    return parts[0] + "".join(word.capitalize() for word in parts[1:])


class CamelModel(BaseModel):
    model_config = ConfigDict(populate_by_name=True, alias_generator=to_camel)


class EmbedFieldDto(CamelModel):
    name: str
    value: str
    inline: bool | None = None

class ButtonStyle(IntEnum):
    primary = 1
    secondary = 2
    success = 3
    danger = 4
    link = 5


class EmbedButtonDto(CamelModel):
    label: str
    url: Optional[str] = None
    custom_id: Optional[str] = Field(default=None, alias="customId")
    emoji: Optional[str] = None
    style: Optional[ButtonStyle] = None
    max_signups: Optional[int] = Field(default=None, alias="maxSignups")
    width: Optional[int] = None
    row_index: Optional[int] = Field(default=None, alias="rowIndex")


class EmbedAuthorDto(CamelModel):
    name: Optional[str] = None
    url: Optional[str] = None
    icon_url: Optional[str] = Field(default=None, alias="iconUrl")


class EmbedBorderDto(CamelModel):
    enabled: bool
    glyph: str
    color: int


class EmbedDto(CamelModel):
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
    border: Optional[EmbedBorderDto] = None

# ---- Chat ----

class Mention(CamelModel):
    id: str
    name: str
    type: str


class AttachmentDto(CamelModel):
    url: str
    filename: Optional[str] = None
    content_type: Optional[str] = Field(default=None, alias="contentType")
    size: Optional[int] = None


class ButtonComponentDto(CamelModel):
    label: str
    custom_id: Optional[str] = Field(default=None, alias="customId")
    url: Optional[str] = None
    style: ButtonStyle
    emoji: Optional[str] = None
    row_index: Optional[int] = Field(default=None, alias="rowIndex")


class MessageAuthor(CamelModel):
    id: str
    name: str
    avatar_url: Optional[str] = Field(default=None, alias="avatarUrl")
    use_character_name: bool | None = Field(default=False, alias="useCharacterName")


class ReactionDto(CamelModel):
    emoji: str
    emoji_id: str | None = Field(default=None, alias="emojiId")
    is_animated: bool = Field(alias="isAnimated")
    count: int
    me: bool


class MessageReferenceDto(CamelModel):
    message_id: str = Field(alias="messageId")
    channel_id: str = Field(alias="channelId")


class ChatMessage(CamelModel):
    cursor: int | None = None
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


class PresenceDto(CamelModel):
    id: str
    name: str
    status: str
    avatar_url: str | None = Field(default=None, alias="avatarUrl")
    roles: List[str] = Field(default_factory=list)


class TemplatePayload(CamelModel):
    channel_id: str = Field(alias="channelId")
    title: str
    time: str | None = None
    description: str
    url: str | None = None
    image_url: str | None = Field(default=None, alias="imageUrl")
    image_id: str | None = Field(default=None, alias="imageId")
    image_filename: str | None = Field(default=None, alias="imageFilename")
    image_content_type: str | None = Field(
        default=None, alias="imageContentType"
    )
    image_size: int | None = Field(default=None, alias="imageSize")
    thumbnail_url: str | None = Field(default=None, alias="thumbnailUrl")
    thumbnail_id: str | None = Field(default=None, alias="thumbnailId")
    thumbnail_filename: str | None = Field(
        default=None, alias="thumbnailFilename"
    )
    thumbnail_content_type: str | None = Field(
        default=None, alias="thumbnailContentType"
    )
    thumbnail_size: int | None = Field(default=None, alias="thumbnailSize")
    color: int | None = None
    fields: List[EmbedFieldDto] | None = None
    buttons: List[EmbedButtonDto] | None = None
    attendance: List[str] | None = None
    mentions: List[str] | None = None
    repeat: str | None = None
    embeds: List[dict] | None = None
    attachments: List[AttachmentDto] | None = None


class TemplateDto(CamelModel):
    id: str
    name: str
    description: str | None = None
    payload: TemplatePayload
    updated_at: datetime = Field(alias="updatedAt")


# ---- Notepad ----


class NotePageDto(CamelModel):
    id: str
    section_id: str = Field(alias="sectionId")
    title: str
    content: str
    order: int
    color: int | None = None
    created_by_id: str | None = Field(default=None, alias="createdById")
    updated_by_id: str | None = Field(default=None, alias="updatedById")
    created_by_discord_id: str | None = Field(
        default=None, alias="createdByDiscordId"
    )
    updated_by_discord_id: str | None = Field(
        default=None, alias="updatedByDiscordId"
    )
    created_by_display_name: str | None = Field(
        default=None, alias="createdByDisplayName"
    )
    updated_by_display_name: str | None = Field(
        default=None, alias="updatedByDisplayName"
    )
    created_at: datetime = Field(alias="createdAt")
    updated_at: datetime = Field(alias="updatedAt")
    version: int


class NoteSectionDto(CamelModel):
    id: str
    name: str
    order: int
    color: int | None = None
    created_by_id: str | None = Field(default=None, alias="createdById")
    updated_by_id: str | None = Field(default=None, alias="updatedById")
    created_by_discord_id: str | None = Field(
        default=None, alias="createdByDiscordId"
    )
    updated_by_discord_id: str | None = Field(
        default=None, alias="updatedByDiscordId"
    )
    created_by_display_name: str | None = Field(
        default=None, alias="createdByDisplayName"
    )
    updated_by_display_name: str | None = Field(
        default=None, alias="updatedByDisplayName"
    )
    created_at: datetime = Field(alias="createdAt")
    updated_at: datetime = Field(alias="updatedAt")
    version: int
    pages: List[NotePageDto] = Field(default_factory=list)


class NotepadStateDto(CamelModel):
    sections: List[NoteSectionDto] = Field(default_factory=list)


class NoteSectionCreateBody(CamelModel):
    name: str
    color: int | None = None


class NoteSectionUpdateBody(CamelModel):
    name: str | None = None
    color: int | None = None
    version: int


class NoteSectionReorderBody(CamelModel):
    section_ids: List[str] = Field(alias="sectionIds")


class NotePageCreateBody(CamelModel):
    section_id: str = Field(alias="sectionId")
    title: str
    content: str = ""
    color: int | None = None


class NotePageUpdateBody(CamelModel):
    title: str | None = None
    color: int | None = None
    version: int


class NotePageReorderEntry(CamelModel):
    section_id: str = Field(alias="sectionId")
    page_ids: List[str] = Field(default_factory=list, alias="pageIds")


class NotePageReorderBody(CamelModel):
    sections: List[NotePageReorderEntry] = Field(default_factory=list)


class NotePageContentBody(CamelModel):
    content: str
    version: int
