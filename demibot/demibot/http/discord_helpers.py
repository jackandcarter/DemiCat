from __future__ import annotations

"""Utility helpers for translating ``discord.py`` objects to API DTOs.

The DemiCat service exposes many Discord concepts through its HTTP API for the
Dalamud plugin.  These helpers centralize the conversion logic so that Discord
objects can be consistently serialized into the pydantic models consumed by the
plugin.
"""

from typing import List

import discord

from .schemas import (
    AttachmentDto,
    ButtonComponentDto,
    ChatMessage,
    EmbedAuthorDto,
    EmbedButtonDto,
    EmbedDto,
    EmbedFieldDto,
    Mention,
    MessageAuthor,
    MessageReferenceDto,
    ReactionDto,
)


def attachment_to_dto(attachment: discord.Attachment) -> AttachmentDto:
    """Convert a Discord attachment into an :class:`AttachmentDto`."""
    return AttachmentDto(
        url=attachment.url,
        filename=attachment.filename,
        contentType=attachment.content_type,
    )


def mention_to_dto(user: discord.abc.User) -> Mention:
    """Convert a Discord user/member into a :class:`Mention`."""
    name = getattr(user, "display_name", None) or getattr(user, "name", "")
    return Mention(id=str(user.id), name=name, kind="user")


def role_mention_to_dto(role: discord.Role) -> Mention:
    """Convert a Discord role into a :class:`Mention`."""
    return Mention(id=str(role.id), name=role.name, kind="role")


def channel_mention_to_dto(channel: discord.abc.GuildChannel) -> Mention:
    """Convert a Discord channel into a :class:`Mention`."""
    name = getattr(channel, "name", "")
    return Mention(id=str(channel.id), name=name, kind="channel")


def reaction_to_dto(reaction: discord.Reaction) -> ReactionDto:
    """Convert a :class:`discord.Reaction` into :class:`ReactionDto`."""
    emoji = reaction.emoji
    emoji_str: str
    emoji_id: str | None = None
    animated = False
    if isinstance(emoji, discord.PartialEmoji):
        emoji_str = str(emoji)
        emoji_id = str(emoji.id) if emoji.id else None
        animated = bool(emoji.animated)
    elif isinstance(emoji, discord.Emoji):
        emoji_str = str(emoji)
        emoji_id = str(emoji.id)
        animated = bool(emoji.animated)
    else:  # str or others
        emoji_str = str(emoji)
    return ReactionDto(
        emoji=emoji_str,
        emojiId=emoji_id,
        isAnimated=animated,
        count=reaction.count,
        me=reaction.me,
    )


def components_to_dtos(message: discord.Message) -> List[ButtonComponentDto]:
    """Flatten message components into :class:`ButtonComponentDto` objects."""
    out: List[ButtonComponentDto] = []
    for row in getattr(message, "components", []) or []:
        children = getattr(row, "children", None) or getattr(row, "components", [])
        for comp in children or []:
            if getattr(comp, "type", None) == 2:  # button
                style = getattr(comp, "style", None)
                style_val = style.value if hasattr(style, "value") else style
                emoji = getattr(comp, "emoji", None)
                emoji_str = str(emoji) if emoji else None
                out.append(
                    ButtonComponentDto(
                        label=getattr(comp, "label", ""),
                        customId=getattr(comp, "custom_id", None),
                        url=getattr(comp, "url", None),
                        style=style_val,
                        emoji=emoji_str,
                    )
                )
    return out


def extract_embed_buttons(components: List[ButtonComponentDto]) -> List[EmbedButtonDto]:
    """Convert message button components to :class:`EmbedButtonDto`."""
    return [
        EmbedButtonDto(
            label=c.label,
            url=c.url,
            customId=c.customId,
            emoji=c.emoji,
            style=c.style,
        )
        for c in components
    ]


def embed_to_dto(
    message: discord.Message,
    embed: discord.Embed,
    buttons: List[EmbedButtonDto] | None = None,
) -> EmbedDto:
    """Convert a Discord embed to :class:`EmbedDto`.

    Parameters
    ----------
    message:
        The parent Discord message.  Used for the message ID, channel ID and
        mention extraction.
    embed:
        The embed to convert.
    buttons:
        Optional list of button components associated with the embed.
    """

    data = embed.to_dict()
    footer_data = data.get("footer", {}) or {}
    provider_data = data.get("provider", {}) or {}
    video_data = data.get("video", {}) or {}

    author_list: List[dict] = []
    first_author = data.get("author")
    if first_author:
        author_list.append(first_author)
    author_list.extend(data.get("authors", []))

    authors = [
        EmbedAuthorDto(
            name=a.get("name"),
            url=a.get("url"),
            iconUrl=a.get("icon_url"),
        )
        for a in author_list
        if a
    ] or None

    return EmbedDto(
        id=str(message.id),
        timestamp=embed.timestamp,
        color=embed.color.value if embed.color else None,
        authorName=first_author.get("name") if first_author else None,
        authorIconUrl=first_author.get("icon_url") if first_author else None,
        authors=authors,
        title=embed.title,
        description=embed.description,
        url=embed.url,
        fields=[
            EmbedFieldDto(name=f.name, value=f.value, inline=f.inline)
            for f in embed.fields
        ]
        or None,
        thumbnailUrl=embed.thumbnail.url if embed.thumbnail else None,
        imageUrl=embed.image.url if embed.image else None,
        providerName=provider_data.get("name"),
        providerUrl=provider_data.get("url"),
        footerText=footer_data.get("text"),
        footerIconUrl=footer_data.get("icon_url"),
        videoUrl=video_data.get("url"),
        videoWidth=video_data.get("width"),
        videoHeight=video_data.get("height"),
        buttons=buttons or None,
        channelId=message.channel.id if hasattr(message, "channel") else None,
        mentions=(
            [m.id for m in message.mentions]
            + [r.id for r in getattr(message, "role_mentions", [])]
            + [c.id for c in getattr(message, "channel_mentions", [])]
        )
        or None,
    )


def message_to_chat_message(message: discord.Message) -> ChatMessage:
    """Convert a :class:`discord.Message` into a :class:`ChatMessage` DTO."""

    attachments = [attachment_to_dto(a) for a in message.attachments] or None

    user_mentions = [
        mention_to_dto(m)
        for m in getattr(message, "mentions", [])
        if not getattr(m, "bot", False)
    ]
    role_mentions = [role_mention_to_dto(r) for r in getattr(message, "role_mentions", [])]
    channel_mentions = [
        channel_mention_to_dto(c)
        for c in getattr(message, "channel_mentions", [])
    ]
    mentions = user_mentions + role_mentions + channel_mentions or None

    author = MessageAuthor(
        id=str(message.author.id),
        name=message.author.display_name or message.author.name,
        avatarUrl=(
            str(message.author.display_avatar.url)
            if message.author.display_avatar
            else None
        ),
    )

    components = components_to_dtos(message)
    embeds = [
        embed_to_dto(message, e, extract_embed_buttons(components))
        for e in message.embeds
    ] or None

    reference = None
    if message.reference:
        reference = MessageReferenceDto(
            messageId=str(message.reference.message_id),
            channelId=str(message.reference.channel_id),
        )

    reactions = [reaction_to_dto(r) for r in getattr(message, "reactions", [])] or None

    return ChatMessage(
        id=str(message.id),
        channelId=str(message.channel.id),
        authorName=author.name,
        authorAvatarUrl=author.avatarUrl,
        timestamp=message.created_at,
        content=message.content,
        attachments=attachments,
        mentions=mentions,
        author=author,
        embeds=embeds,
        reference=reference,
        components=components or None,
        reactions=reactions,
        editedTimestamp=message.edited_at,
    )

