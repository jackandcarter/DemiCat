from __future__ import annotations

"""Utility helpers for translating ``discord.py`` objects to API DTOs.

The DemiCat service exposes many Discord concepts through its HTTP API for the
Dalamud plugin.  These helpers centralize the conversion logic so that Discord
objects can be consistently serialized into the pydantic models consumed by the
plugin.
"""

from typing import Dict, List, Tuple

import json
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
    MessageReferenceDto,
    ReactionDto,
)


def attachment_to_dto(attachment: discord.Attachment) -> AttachmentDto:
    """Convert a Discord attachment into an :class:`AttachmentDto`."""
    return AttachmentDto(
        url=attachment.url,
        filename=attachment.filename,
        content_type=attachment.content_type,
    )


def mention_to_dto(obj: discord.abc.Snowflake) -> Mention:
    """Convert a Discord snowflake into a :class:`Mention` with type."""

    mention_type = "unknown"
    name = getattr(obj, "name", "")

    # First, attempt to use the ``discord`` types directly.  This is the
    # typical code path when running inside the real application where the
    # ``discord.py`` library is available.
    if isinstance(obj, (discord.User, getattr(discord, "Member", object))):
        mention_type = "user"
        name = getattr(obj, "display_name", None) or getattr(obj, "name", "")
    elif isinstance(obj, getattr(discord, "Role", object)):
        mention_type = "role"
    elif isinstance(obj, getattr(discord.abc, "GuildChannel", object)):
        mention_type = "channel"
    else:
        # In the test environment the full ``discord`` module might not be
        # imported (or may have been imported before stubs are installed).
        # Fall back to a light-weight duck-typing approach based on the class
        # name so that simple stand-ins used in tests are recognised.
        cls_name = obj.__class__.__name__.lower()
        if "user" in cls_name or "member" in cls_name:
            mention_type = "user"
            name = getattr(obj, "display_name", None) or getattr(obj, "name", "")
        elif "role" in cls_name:
            mention_type = "role"
        elif "channel" in cls_name:
            mention_type = "channel"

    return Mention(id=str(obj.id), name=name, type=mention_type)


def reaction_to_dto(reaction: discord.Reaction) -> ReactionDto:
    """Convert a Discord reaction into a :class:`ReactionDto`."""
    emoji = reaction.emoji
    emoji_name = getattr(emoji, "name", str(emoji))
    emoji_id = getattr(emoji, "id", None)
    is_animated = getattr(emoji, "animated", False)
    return ReactionDto(
        emoji=emoji_name,
        emoji_id=str(emoji_id) if emoji_id else None,
        is_animated=is_animated,
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
            icon_url=a.get("icon_url"),
        )
        for a in author_list
        if a
    ] or None

    return EmbedDto(
        id=str(message.id),
        timestamp=embed.timestamp,
        color=embed.color.value if embed.color else None,
        author_name=first_author.get("name") if first_author else None,
        author_icon_url=first_author.get("icon_url") if first_author else None,
        authors=authors,
        title=embed.title,
        description=embed.description,
        url=embed.url,
        fields=[
            EmbedFieldDto(name=f.name, value=f.value, inline=f.inline)
            for f in embed.fields
        ]
        or None,
        thumbnail_url=embed.thumbnail.url if embed.thumbnail else None,
        image_url=embed.image.url if embed.image else None,
        provider_name=provider_data.get("name"),
        provider_url=provider_data.get("url"),
        footer_text=footer_data.get("text"),
        footer_icon_url=footer_data.get("icon_url"),
        video_url=video_data.get("url"),
        video_width=video_data.get("width"),
        video_height=video_data.get("height"),
        buttons=buttons or None,
        channel_id=message.channel.id if hasattr(message, "channel") else None,
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
    for row_index, row in enumerate(getattr(message, "components", []) or []):
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
                    custom_id=getattr(comp, "custom_id", None),
                    url=getattr(comp, "url", None),
                    style=style_val,
                    emoji=emoji_str,
                    row_index=row_index,
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
        for row_index, row in enumerate(getattr(message, "components", []) or []):
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
                        custom_id=getattr(comp, "custom_id", None),
                        label=getattr(comp, "label", None),
                        style=style_val,
                        emoji=emoji_str,
                        url=getattr(comp, "url", None),
                        row_index=row_index,
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


def serialize_message(
    message: discord.Message,
) -> Tuple[ChatMessage, Dict[str, str | None]]:
    """Serialize a Discord message into DTO and DB JSON fragments."""

    attachments = [attachment_to_dto(a) for a in message.attachments] or None
    mentions_list = [
        mention_to_dto(m)
        for m in message.mentions
        if not getattr(m, "bot", False)
    ]
    mentions_list.extend(mention_to_dto(r) for r in getattr(message, "role_mentions", []) or [])
    mentions_list.extend(
        mention_to_dto(c) for c in getattr(message, "channel_mentions", []) or []
    )
    mentions = mentions_list or None

    author = MessageAuthor(
        id=str(message.author.id),
        name=message.author.display_name or message.author.name,
        avatar_url=(
            str(message.author.display_avatar.url)
            if getattr(message.author, "display_avatar", None)
            else None
        ),
    )

    embeds = None
    embeds_json = None
    if message.embeds:
        buttons = extract_embed_buttons(message)
        embeds_list: list[EmbedDto] = []
        for emb in message.embeds:
            try:
                embeds_list.append(embed_to_dto(message, emb, buttons or None))
            except Exception:
                continue
        embeds = embeds_list or None
        if embeds:
            embeds_json = json.dumps(
                [e.model_dump(mode="json", by_alias=True, exclude_none=True) for e in embeds]
            )

    reference = None
    reference_json = None
    if message.reference:
        reference = MessageReferenceDto(
            message_id=str(message.reference.message_id),
            channel_id=str(message.reference.channel_id),
        )
        reference_json = reference.model_dump_json(by_alias=True, exclude_none=True)

    components = None
    components_json = None
    if getattr(message, "components", None):
        try:
            components = components_to_dtos(message)
        except Exception:
            components = None
        if components:
            components_json = json.dumps(
                [c.model_dump(by_alias=True, exclude_none=True) for c in components]
            )

    reactions = [reaction_to_dto(r) for r in getattr(message, "reactions", [])] or None
    reactions_json = None
    if reactions:
        reactions_json = json.dumps(
            [r.model_dump(by_alias=True, exclude_none=True) for r in reactions]
        )

    dto = ChatMessage(
        id=str(message.id),
        channel_id=str(message.channel.id),
        author_name=author.name,
        author_avatar_url=author.avatar_url,
        timestamp=message.created_at,
        content=message.content,
        attachments=attachments,
        mentions=mentions,
        author=author,
        embeds=embeds,
        reference=reference,
        components=components,
        reactions=reactions,
        edited_timestamp=message.edited_at,
    )

    fragments: Dict[str, str | None] = {
        "attachments_json": json.dumps(
            [a.model_dump(by_alias=True, exclude_none=True) for a in attachments]
        )
        if attachments
        else None,
        "mentions_json": json.dumps(
            [m.model_dump(by_alias=True, exclude_none=True) for m in mentions]
        )
        if mentions
        else None,
        "author_json": author.model_dump_json(by_alias=True, exclude_none=True),
        "embeds_json": embeds_json,
        "reference_json": reference_json,
        "components_json": components_json,
        "reactions_json": reactions_json,
    }

    return dto, fragments


def message_to_chat_message(message: discord.Message) -> ChatMessage:
    """Convert a :class:`discord.Message` into a :class:`ChatMessage` DTO."""

    dto, _ = serialize_message(message)
    return dto

