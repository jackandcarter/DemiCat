from __future__ import annotations

"""Utility helpers for translating ``discord.py`` objects to API DTOs.

The DemiCat service exposes many Discord concepts through its HTTP API for the
Dalamud plugin.  These helpers centralize the conversion logic so that Discord
objects can be consistently serialized into the pydantic models consumed by the
plugin.
"""

from typing import List

import logging
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
    return Mention(id=str(user.id), name=name)


def reaction_to_dto(reaction: discord.Reaction) -> ReactionDto:
    """Convert a Discord reaction into a :class:`ReactionDto`."""
    emoji = reaction.emoji
    emoji_name = getattr(emoji, "name", str(emoji))
    emoji_id = getattr(emoji, "id", None)
    is_animated = getattr(emoji, "animated", False)
    return ReactionDto(
        emoji=emoji_name,
        emojiId=str(emoji_id) if emoji_id else None,
        isAnimated=is_animated,
        count=reaction.count,
        me=reaction.me,
    )


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
        mentions=[m.id for m in message.mentions] or None,
    )


def components_to_dtos(message: discord.Message) -> List[ButtonComponentDto] | None:
    """Flatten message component rows into :class:`ButtonComponentDto` objects.

    Discord exposes components as a list of action rows which in turn contain the
    actual interactive elements (buttons, selects, ...).  The Dalamud plugin only
    understands simple buttons, so this helper extracts those and converts them to
    our API DTOs.
    """

    components: list[ButtonComponentDto] = []
    for row in getattr(message, "components", []) or []:
        children = getattr(row, "children", None) or getattr(row, "components", [])
        for comp in children or []:
            if getattr(comp, "type", None) != 2:  # 2 == button
                continue
            style = getattr(comp, "style", None)
            style_val = style.value if hasattr(style, "value") else style
            emoji = getattr(comp, "emoji", None)
            emoji_str = str(emoji) if emoji else None
            components.append(
                ButtonComponentDto(
                    label=getattr(comp, "label", None),
                    customId=getattr(comp, "custom_id", None),
                    url=getattr(comp, "url", None),
                    style=style_val,
                    emoji=emoji_str,
                )
            )
    return components or None


def extract_embed_buttons(message: discord.Message) -> List[EmbedButtonDto]:
    """Extract button components from a message for embedding.

    Parameters
    ----------
    message:
        The Discord message whose components should be inspected.
    """

    buttons: list[EmbedButtonDto] = []
    try:
        for row in getattr(message, "components", []) or []:
            children = getattr(row, "children", None) or getattr(row, "components", [])
            for comp in children or []:
                if getattr(comp, "type", None) != 2:  # 2 == button
                    continue
                style = getattr(comp, "style", None)
                style_val = style.value if hasattr(style, "value") else style
                emoji = getattr(comp, "emoji", None)
                emoji_str = str(emoji) if emoji else None
                buttons.append(
                    EmbedButtonDto(
                        customId=getattr(comp, "custom_id", None),
                        label=getattr(comp, "label", None),
                        style=style_val,
                        emoji=emoji_str,
                        url=getattr(comp, "url", None),
                    )
                )
    except Exception:
        logging.exception(
            "Button extraction failed for channel %s message %s",
            getattr(getattr(message, "channel", None), "id", None),
            getattr(message, "id", None),
        )
        return []
    return buttons


def message_to_chat_message(message: discord.Message) -> ChatMessage:
    """Convert a :class:`discord.Message` into a :class:`ChatMessage` DTO."""

    attachments = [attachment_to_dto(a) for a in message.attachments] or None
    mentions = [
        mention_to_dto(m)
        for m in message.mentions
        if not getattr(m, "bot", False)
    ] or None

    author = MessageAuthor(
        id=str(message.author.id),
        name=message.author.display_name or message.author.name,
        avatarUrl=(
            str(message.author.display_avatar.url)
            if message.author.display_avatar
            else None
        ),
    )

    embeds = None
    if message.embeds:
        buttons = extract_embed_buttons(message)
        embeds_list: list[EmbedDto] = []
        for emb in message.embeds:
            try:
                embeds_list.append(embed_to_dto(message, emb, buttons or None))
            except Exception:
                continue
        embeds = embeds_list or None

    reference = None
    if message.reference:
        reference = {
            "messageId": message.reference.message_id,
            "channelId": message.reference.channel_id,
            "guildId": message.reference.guild_id,
        }

    components = None
    if getattr(message, "components", None):
        try:
            components = components_to_dtos(message)
        except Exception:
            components = None

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
        components=components,
        reactions=reactions,
        editedTimestamp=message.edited_at,
    )

