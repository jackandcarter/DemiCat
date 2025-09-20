import pytest

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "demibot"
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from demibot.bridge import (
    BRIDGE_MARKER,
    BridgeUpload,
    build_bridge_message,
    extract_bridge_nonce_from_payload,
)
from demibot.db.models import ChannelKind, Membership, User


@pytest.fixture()
def sample_user():
    return User(
        id=1,
        discord_user_id=111,
        global_name="Sample",
        character_name="Hero",
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

    discord_content, embeds, uploads, nonce = build_bridge_message(
        content=content,
        user=sample_user,
        membership=sample_membership,
        channel_kind=ChannelKind.FC_CHAT,
        attachments=attachments,
    )

    assert discord_content == content
    assert nonce
    assert len(embeds) == 1

    embed_dict = embeds[0].to_dict()
    footer_text = embed_dict.get("footer", {}).get("text")
    assert footer_text and footer_text.endswith(f"{BRIDGE_MARKER}{nonce}")
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

    discord_content, embeds, uploads, nonce = build_bridge_message(
        content=long_content,
        user=sample_user,
        membership=sample_membership,
        channel_kind=ChannelKind.FC_CHAT,
    )

    assert discord_content == long_content[:2000]
    assert not uploads
    assert nonce
    assert len(embeds) >= 2

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
