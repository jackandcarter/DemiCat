import pytest

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "demibot"
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from demibot.bridge import (
    BRIDGE_MARKER,
    BridgeUpload,
    DISCORD_CONTENT_LIMIT,
    build_bridge_message,
    extract_bridge_nonce_from_payload,
)
from demibot.db.models import ChannelKind, Membership, User
from datetime import datetime, timezone


@pytest.fixture()
def sample_user():
    return User(
        id=1,
        discord_user_id=111,
        global_name="Sample",
        character_name="Hero",
        world="Eorzea",
    )


@pytest.fixture()
def sample_membership():
    return Membership(
        id=1,
        guild_id=1,
        user_id=1,
        nickname="Nick",
        avatar_url="https://example.com/avatar.png",
    )


def test_build_bridge_message_populates_embed_footer(sample_user, sample_membership):
    content = "Hello from DemiCat"
    attachments = [("screenshot.png", b"bytes", "image/png")]
    fixed_time = datetime(2024, 1, 2, 3, 4, 5, tzinfo=timezone.utc)

    discord_content, embeds, uploads, nonce = build_bridge_message(
        content=content,
        user=sample_user,
        membership=sample_membership,
        channel_kind=ChannelKind.FC_CHAT,
        attachments=attachments,
        timestamp=fixed_time,
    )

    expected_header = "Message Sent by: Nick @ 2024-01-02 03:04:05 UTC"
    header, body = discord_content.split("\n", 1)
    assert header == expected_header
    assert body == content
    assert nonce
    assert len(embeds) == 1

    embed_dict = embeds[0].to_dict()
    footer_text = embed_dict.get("footer", {}).get("text")
    assert footer_text and footer_text.endswith(f"{BRIDGE_MARKER}{nonce}")
    description = embed_dict.get("description", "")
    assert description.startswith(expected_header)
    assert description.split("\n", 1)[1] == content
    assert embed_dict.get("author", {}).get("name") == sample_membership.nickname
    assert embed_dict.get("author", {}).get("icon_url") == sample_membership.avatar_url

    assert embed_dict.get("image", {}).get("url") == "attachment://screenshot.png"

    assert len(uploads) == 1
    upload = uploads[0]
    assert isinstance(upload, BridgeUpload)
    assert upload.filename == "screenshot.png"
    assert upload.content_type == "image/png"
    assert upload.data == b"bytes"

    payload = {"embeds": [embed_dict]}
    assert extract_bridge_nonce_from_payload(payload) == nonce


def test_build_bridge_message_splits_long_content(sample_user, sample_membership):
    long_content = "A" * 7000
    fixed_time = datetime(2024, 5, 6, 7, 8, 9, tzinfo=timezone.utc)

    discord_content, embeds, uploads, nonce = build_bridge_message(
        content=long_content,
        user=sample_user,
        membership=sample_membership,
        channel_kind=ChannelKind.FC_CHAT,
        timestamp=fixed_time,
    )

    expected_header = "Message Sent by: Nick @ 2024-05-06 07:08:09 UTC"
    assert discord_content.startswith(f"{expected_header}\n")
    header_len = len(expected_header) + 1
    assert (
        discord_content[header_len:]
        == long_content[: DISCORD_CONTENT_LIMIT - header_len]
    )
    assert not uploads
    assert nonce
    assert len(embeds) >= 2

    first_description = embeds[0].to_dict().get("description", "")
    assert first_description.startswith(expected_header)

    total_length = 0
    for embed in embeds:
        data = embed.to_dict()
        description = data.get("description", "")
        assert len(description) <= 4096
        total_length += len(description)
        assert data.get("footer", {}).get("text", "").endswith(f"{BRIDGE_MARKER}{nonce}")

    assert total_length <= 6000
    assert embeds[-1].to_dict().get("description", "").endswith("…")

    payload = {"embeds": [embed.to_dict() for embed in embeds]}
    assert extract_bridge_nonce_from_payload(payload) == nonce


def test_build_bridge_message_includes_character_when_enabled(
    sample_user, sample_membership
):
    fixed_time = datetime(2024, 7, 8, 9, 10, 11, tzinfo=timezone.utc)

    discord_content, embeds, uploads, nonce = build_bridge_message(
        content="Hi",
        user=sample_user,
        membership=sample_membership,
        channel_kind=ChannelKind.FC_CHAT,
        use_character_name=True,
        timestamp=fixed_time,
    )

    expected_header = (
        "Message Sent by: Nick / Hero / Eorzea @ 2024-07-08 09:10:11 UTC"
    )
    assert discord_content.startswith(f"{expected_header}\n")
    assert embeds[0].to_dict().get("description", "").startswith(expected_header)
    assert not uploads
    assert nonce


def test_build_bridge_message_preserves_existing_header(sample_user, sample_membership):
    existing_header = "Message Sent by: Nick @ 2024-01-02 03:04:05 UTC"
    body = "Preformatted content"
    preformatted = f"{existing_header}\n{body}"
    server_timestamp = datetime(2024, 12, 11, 10, 9, 8, tzinfo=timezone.utc)

    discord_content, embeds, uploads, nonce = build_bridge_message(
        content=preformatted,
        user=sample_user,
        membership=sample_membership,
        channel_kind=ChannelKind.FC_CHAT,
        timestamp=server_timestamp,
    )

    assert discord_content == preformatted
    assert discord_content.count("Message Sent by:") == 1
    assert not uploads
    assert nonce

    description = embeds[0].to_dict().get("description", "")
    assert description == preformatted
