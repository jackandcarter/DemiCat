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


def test_build_bridge_message_populates_embed_metadata(sample_user, sample_membership):
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

    assert discord_content == ""
    assert nonce
    assert len(embeds) == 1

    embed_dict = embeds[0].to_dict()
    provider_text = embed_dict.get("provider", {}).get("name")
    assert provider_text == f"{BRIDGE_MARKER}{nonce}"
    description = embed_dict.get("description", "")
    assert description == content
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

    assert discord_content
    assert not uploads
    assert nonce
    assert len(embeds) >= 2

    embed_descriptions = [
        embed.to_dict().get("description", "") for embed in embeds
    ]
    combined = "".join(embed_descriptions)
    body_in_embeds = combined

    total_length = 0
    for embed in embeds:
        data = embed.to_dict()
        description = data.get("description", "")
        assert len(description) <= 4096
        total_length += len(description)
        assert data.get("provider", {}).get("name") == f"{BRIDGE_MARKER}{nonce}"

    assert total_length <= 6000
    last_description = embeds[-1].to_dict().get("description", "")
    assert last_description.endswith("…")
    if body_in_embeds.endswith("…"):
        body_in_embeds_without_ellipsis = body_in_embeds[:-1]
    else:
        body_in_embeds_without_ellipsis = body_in_embeds

    expected_leftover = long_content[len(body_in_embeds_without_ellipsis) :]
    assert (
        discord_content
        == expected_leftover[: DISCORD_CONTENT_LIMIT]
    )

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

    assert discord_content == ""
    embed_dict = embeds[0].to_dict()
    assert embed_dict.get("description", "") == "Hi"
    assert embed_dict.get("author", {}).get("name") == sample_user.character_name
    assert embed_dict.get("provider", {}).get("name", "").startswith(BRIDGE_MARKER)
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

    assert discord_content == ""
    assert not uploads
    assert nonce

    description = embeds[0].to_dict().get("description", "")
    assert description == preformatted

