from __future__ import annotations

import io
import math
import uuid
from dataclasses import dataclass
from datetime import datetime
from typing import Iterable, Mapping, Sequence

import discord

from .db.models import ChannelKind, Membership, User

BRIDGE_MARKER = "bridge:demicat nonce:"
DISCORD_CONTENT_LIMIT = 2000
DISCORD_EMBED_DESCRIPTION_LIMIT = 4096
DISCORD_EMBED_TOTAL_LIMIT = 6000
DISCORD_EMBED_COUNT_LIMIT = 10
IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}
DEFAULT_FC_COLOR = 0x5865F2
DEFAULT_OFFICER_COLOR = 0xED4245
MAX_BORDER_LINE_LENGTH = 120
DEFAULT_EMBED_BORDER_GLYPH = "⬛"
GLYPH_ALIASES = {
    "square": DEFAULT_EMBED_BORDER_GLYPH,
    "circle": "⚫",
    "triangle": "🔺",
}


@dataclass
class BridgeUpload:
    """In-memory representation of an attachment destined for Discord."""

    filename: str
    data: bytes
    content_type: str | None = None

    def to_discord_file(self) -> discord.File:
        """Return a fresh :class:`discord.File` for this upload."""

        return discord.File(io.BytesIO(self.data), filename=self.filename)


def _get_embed_type() -> type:
    embed_type = getattr(discord, "Embed", None)
    if embed_type is not None:
        return embed_type

    class _FallbackEmbed:
        def __init__(self, *, description: str | None = None, color=None):
            self._data: dict[str, object] = {}
            if description is not None:
                self._data["description"] = description
            if color is not None:
                value = getattr(color, "value", color)
                self._data["color"] = value

        def set_author(
            self, *, name: str | None = None, icon_url: str | None = None
        ) -> None:
            author: dict[str, object] = {}
            if name is not None:
                author["name"] = name
            if icon_url is not None:
                author["icon_url"] = icon_url
            if author:
                self._data.setdefault("author", {}).update(author)

        def set_image(self, *, url: str | None = None) -> None:
            if url is not None:
                self._data.setdefault("image", {})["url"] = url

        def to_dict(self) -> dict[str, object]:
            return dict(self._data)

    return _FallbackEmbed


def _is_image(filename: str, content_type: str | None) -> bool:
    if content_type and content_type.startswith("image/"):
        return True
    lower = filename.lower()
    for ext in IMAGE_EXTENSIONS:
        if lower.endswith(ext):
            return True
    return False


def _determine_author_name(
    *,
    user: User,
    membership: Membership | None,
    use_character_name: bool,
) -> str:
    if use_character_name and user.character_name:
        return user.character_name
    if membership and membership.nickname:
        return membership.nickname
    if user.global_name:
        return user.global_name
    if user.character_name:
        return user.character_name
    return str(user.discord_user_id)


def _determine_display_name(*, user: User, membership: Membership | None) -> str:
    if membership and membership.nickname:
        nickname = membership.nickname.strip()
        if nickname:
            return nickname
    if user.global_name:
        global_name = user.global_name.strip()
        if global_name:
            return global_name
    return str(user.discord_user_id)


def _normalize_content(content: str) -> str:
    return content.replace("\r\n", "\n").replace("\r", "\n")


def _split_embed_text(text: str) -> tuple[list[str], int]:
    """Split ``text`` into chunks that satisfy Discord's embed limits.

    Returns
    -------
    tuple[list[str], int]
        A tuple containing the resulting embed description chunks and the
        number of characters from ``text`` that are represented in those
        chunks (excluding any ellipsis added to signal truncation).
    """

    if not text:
        return [], 0

    chunks: list[str] = []
    remaining = text
    total = 0
    consumed = 0
    while (
        remaining
        and len(chunks) < DISCORD_EMBED_COUNT_LIMIT
        and total < DISCORD_EMBED_TOTAL_LIMIT
    ):
        remaining_total = DISCORD_EMBED_TOTAL_LIMIT - total
        take = min(
            DISCORD_EMBED_DESCRIPTION_LIMIT,
            remaining_total,
            len(remaining),
        )
        if take <= 0:
            break
        slice_text = remaining[:take]
        if len(remaining) > take:
            # Prefer splitting at a newline or space when possible to avoid
            # breaking words mid-stream.
            split_pos = max(slice_text.rfind("\n"), slice_text.rfind(" "))
            if split_pos > 0:
                slice_text = slice_text[:split_pos]
        if not slice_text:
            slice_text = remaining[:take]
        actual = len(slice_text)
        chunks.append(slice_text)
        remaining = remaining[actual:]
        total += actual
        consumed += actual

    if remaining:
        # Append an ellipsis to signal truncation while respecting limits.
        removed_from_last = 0
        if chunks:
            last = chunks[-1]
            if len(last) >= DISCORD_EMBED_DESCRIPTION_LIMIT:
                last = last[:-1]
                removed_from_last += 1
            else:
                space_available = min(
                    DISCORD_EMBED_DESCRIPTION_LIMIT - len(last),
                    DISCORD_EMBED_TOTAL_LIMIT - total,
                )
                if space_available <= 0 and len(last) > 0:
                    last = last[:-1]
                    removed_from_last += 1
            chunks[-1] = f"{last}…" if last else "…"
            consumed -= removed_from_last
        else:
            truncated = remaining[: DISCORD_EMBED_DESCRIPTION_LIMIT - 1]
            chunks.append(f"{truncated}…")
            consumed += len(truncated)
        consumed = max(consumed, 0)
    return chunks, consumed


def _sanitize_embed_color(value: int | None) -> int | None:
    if value is None:
        return None
    try:
        numeric = int(value)
    except (TypeError, ValueError):
        return None
    if numeric < 0:
        return 0
    if numeric > 0xFFFFFF:
        return 0xFFFFFF
    return numeric


def _normalize_embed_border_glyph(value: object) -> str:
    if value is None:
        return DEFAULT_EMBED_BORDER_GLYPH

    glyph = str(value).strip()
    if not glyph:
        return DEFAULT_EMBED_BORDER_GLYPH

    alias = glyph.lower()
    return GLYPH_ALIASES.get(alias, glyph)


def _sanitize_embed_border(
    settings: Mapping[str, object] | None, *, channel_kind: ChannelKind
) -> tuple[bool, str, int]:
    if not isinstance(settings, Mapping):
        default_color = (
            DEFAULT_OFFICER_COLOR
            if channel_kind == ChannelKind.OFFICER_CHAT
            else DEFAULT_FC_COLOR
        )
        return False, DEFAULT_EMBED_BORDER_GLYPH, default_color

    enabled = bool(settings.get("enabled"))
    glyph_value = _normalize_embed_border_glyph(settings.get("glyph"))

    color_value = _sanitize_embed_color(settings.get("color"))
    if color_value is None:
        color_value = (
            DEFAULT_OFFICER_COLOR
            if channel_kind == ChannelKind.OFFICER_CHAT
            else DEFAULT_FC_COLOR
        )
    color_value &= 0xFFFFFF
    return enabled, glyph_value, color_value


def _apply_embed_border(
    text: str,
    settings: Mapping[str, object] | None,
    *,
    channel_kind: ChannelKind,
) -> tuple[str, bool, str | None]:
    if not text.strip():
        return text, False, None

    enabled, glyph, _ = _sanitize_embed_border(settings, channel_kind=channel_kind)
    if not enabled:
        return text, False, None

    lines = text.split("\n")
    if any(len(line) > MAX_BORDER_LINE_LENGTH for line in lines):
        return (
            text,
            False,
            f"Embed border disabled because a line exceeds {MAX_BORDER_LINE_LENGTH} characters.",
        )

    width = max(1, max(len(line) for line in lines))
    glyph_symbol = glyph or DEFAULT_EMBED_BORDER_GLYPH
    glyph_length = max(len(glyph_symbol), 1)
    row_template = f"{glyph_symbol} {' ' * width} {glyph_symbol}"
    row_length = len(row_template)
    horizontal_count = max(1, math.ceil(row_length / glyph_length))
    top = glyph_symbol * horizontal_count
    bordered_lines = [top]
    for line in lines:
        bordered_lines.append(f"{glyph_symbol} {line.ljust(width)} {glyph_symbol}")
    bordered_lines.append(top)
    bordered = "\n".join(bordered_lines)

    if len(bordered) > DISCORD_EMBED_DESCRIPTION_LIMIT:
        return (
            text,
            False,
            "Embed border disabled because it exceeds Discord's embed length limit.",
        )

    return bordered, True, None


def build_bridge_message(
    *,
    content: str,
    user: User,
    membership: Membership | None,
    channel_kind: ChannelKind,
    use_character_name: bool = False,
    attachments: Sequence[tuple[str, bytes, str | None]] | None = None,
    nonce: str | None = None,
    timestamp: datetime | None = None,
    embed_color: int | None = None,
    embed_border: Mapping[str, object] | None = None,
) -> tuple[str, list[discord.Embed], list[BridgeUpload], str]:
    """Construct the Discord payload for a bridge message."""

    normalized = _normalize_content(content or "")
    uploads: list[BridgeUpload] = []
    image_filename: str | None = None
    if attachments:
        for name, data, content_type in attachments:
            filename = name or "attachment"
            uploads.append(
                BridgeUpload(filename=filename, data=data, content_type=content_type)
            )
            if image_filename is None and _is_image(filename, content_type):
                image_filename = filename

    display_name = _determine_display_name(user=user, membership=membership)
    character_name = user.character_name if use_character_name else None
    world_name = user.world if use_character_name else None

    bordered_text, border_applied, _ = _apply_embed_border(
        normalized, embed_border, channel_kind=channel_kind
    )
    embed_source = bordered_text if border_applied else normalized

    chunks, represented = _split_embed_text(embed_source)
    if not chunks:
        chunks = [""]

    embed_nonce = nonce or uuid.uuid4().hex
    author_name = _determine_author_name(
        user=user, membership=membership, use_character_name=use_character_name
    )
    author_icon = membership.avatar_url if membership else None

    override_color = _sanitize_embed_color(embed_color)
    color_value = override_color
    if color_value is None:
        if channel_kind == ChannelKind.OFFICER_CHAT:
            color_value = DEFAULT_OFFICER_COLOR
        else:
            color_value = DEFAULT_FC_COLOR
    color_factory = getattr(discord, "Color", None) or getattr(discord, "Colour", None)
    color = color_factory(color_value) if callable(color_factory) else color_value

    embed_type = _get_embed_type()
    embeds: list[discord.Embed] = []
    for index, chunk in enumerate(chunks):
        embed = embed_type(description=chunk or None, color=color)
        embed.set_author(name=author_name, icon_url=author_icon)
        if index == 0 and image_filename:
            embed.set_image(url=f"attachment://{image_filename}")
        embeds.append(embed)

    leftover = embed_source[represented:]
    if leftover.startswith("\n"):
        leftover = leftover[1:]
    discord_content = leftover[:DISCORD_CONTENT_LIMIT]
    return discord_content, embeds, uploads, embed_nonce


def extract_bridge_nonce_from_footer(text: str | None) -> str | None:
    if not text:
        return None
    marker = BRIDGE_MARKER.lower()
    lower = text.lower()
    idx = lower.find(marker)
    if idx == -1:
        return None
    start = idx + len(marker)
    remainder = text[start:]
    for separator in ("•", "|", "\n"):
        parts = remainder.split(separator, 1)
        if parts:
            remainder = parts[0]
    nonce = remainder.strip()
    if " " in nonce:
        nonce = nonce.split(" ", 1)[0]
    return nonce or None


def extract_bridge_nonce_from_embed_dict(embed: Mapping[str, object]) -> str | None:
    provider = embed.get("provider")
    if isinstance(provider, Mapping):
        provider_name = provider.get("name")
        if isinstance(provider_name, str):
            nonce = extract_bridge_nonce_from_footer(provider_name)
            if nonce:
                return nonce

    for key in ("providerName", "provider_name"):
        provider_name = embed.get(key)
        if isinstance(provider_name, str):
            nonce = extract_bridge_nonce_from_footer(provider_name)
            if nonce:
                return nonce

    footer_text = embed.get("footerText")
    if isinstance(footer_text, str):
        nonce = extract_bridge_nonce_from_footer(footer_text)
        if nonce:
            return nonce
    footer = embed.get("footer")
    if isinstance(footer, Mapping):
        text = footer.get("text")
        if isinstance(text, str):
            nonce = extract_bridge_nonce_from_footer(text)
            if nonce:
                return nonce
    return None


def extract_bridge_nonce_from_payload(payload: Mapping[str, object]) -> str | None:
    raw_nonce = payload.get("nonce")
    if isinstance(raw_nonce, str) and raw_nonce:
        return raw_nonce

    embeds = payload.get("embeds")
    if isinstance(embeds, Iterable):
        for embed in embeds:
            if isinstance(embed, Mapping):
                nonce = extract_bridge_nonce_from_embed_dict(embed)
                if nonce:
                    return nonce
    return None


def extract_bridge_nonce_from_embed(embed: discord.Embed) -> str | None:
    data = embed.to_dict()
    return extract_bridge_nonce_from_embed_dict(data)
