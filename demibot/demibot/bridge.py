from __future__ import annotations

import io
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Iterable, Mapping, Sequence

import discord

from .db.models import ChannelKind, Membership, User

BRIDGE_MARKER = "bridge:demicat nonce:"
DISCORD_CONTENT_LIMIT = 2000
DISCORD_EMBED_DESCRIPTION_LIMIT = 4096
DISCORD_EMBED_TOTAL_LIMIT = 6000
DISCORD_EMBED_COUNT_LIMIT = 10
IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}


@dataclass
class BridgeUpload:
    """In-memory representation of an attachment destined for Discord."""

    filename: str
    data: bytes
    content_type: str | None = None

    def to_discord_file(self) -> discord.File:
        """Return a fresh :class:`discord.File` for this upload."""

        return discord.File(io.BytesIO(self.data), filename=self.filename)


def _tab_label(kind: ChannelKind | None) -> str:
    if kind == ChannelKind.OFFICER_CHAT:
        return "Officer Chat"
    if kind == ChannelKind.FC_CHAT:
        return "FC Chat"
    return "Chat"


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

    timestamp = timestamp or datetime.now(timezone.utc)
    display_name = _determine_display_name(user=user, membership=membership)
    character_name = user.character_name if use_character_name else None
    world_name = user.world if use_character_name else None

    header_present = _has_existing_header(normalized)
    if not header_present:
        header = _format_header_line(
            display_name=display_name,
            use_character_name=use_character_name,
            character_name=character_name,
            world_name=world_name,
            timestamp=timestamp,
        )
        if normalized:
            normalized = f"{header}\n{normalized}"
        else:
            normalized = header


    chunks, represented = _split_embed_text(normalized)
    if not chunks:
        chunks = [""]

    embed_nonce = nonce or uuid.uuid4().hex
    footer = f"DemiCat • {_tab_label(channel_kind)} • {BRIDGE_MARKER}{embed_nonce}"
    author_name = _determine_author_name(
        user=user, membership=membership, use_character_name=use_character_name
    )
    author_icon = membership.avatar_url if membership else None

    color = discord.Color(0x5865F2)
    if channel_kind == ChannelKind.OFFICER_CHAT:
        color = discord.Color(0xED4245)

    embeds: list[discord.Embed] = []
    for index, chunk in enumerate(chunks):
        embed = discord.Embed(description=chunk or None, timestamp=timestamp, color=color)
        embed.set_footer(text=footer)
        embed.set_author(name=author_name, icon_url=author_icon)
        if index == 0 and image_filename:
            embed.set_image(url=f"attachment://{image_filename}")
        embeds.append(embed)

    leftover = normalized[represented:]
    if leftover.startswith("\n"):
        leftover = leftover[1:]
    discord_content = leftover[:DISCORD_CONTENT_LIMIT]
    return discord_content, embeds, uploads, embed_nonce


def _format_header_line(
    *,
    display_name: str,
    use_character_name: bool,
    character_name: str | None,
    world_name: str | None,
    timestamp: datetime,
) -> str:
    name = display_name.strip() or "You"
    segments = [name]
    if use_character_name:
        char = (character_name or "").strip()
        world = (world_name or "").strip()
        if char:
            segments.append(char)
            if world:
                segments.append(world)
        elif world:
            segments.append(world)
    formatted_timestamp = timestamp.astimezone(timezone.utc).strftime(
        "%Y-%m-%d %H:%M:%S UTC"
    )
    return f"Message Sent by: {' / '.join(segments)} @ {formatted_timestamp}"



def _has_existing_header(content: str) -> bool:
    if not content:
        return False
    first_line, _, _ = content.partition("\n")
    line = first_line.strip()
    if not line.startswith("Message Sent by:"):
        return False
    parts = line.rsplit("@", 1)
    if len(parts) != 2:
        return False
    timestamp_text = parts[1].strip()
    if not timestamp_text:
        return False
    try:
        datetime.strptime(timestamp_text, "%Y-%m-%d %H:%M:%S UTC")
    except ValueError:
        return False
    return True



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
