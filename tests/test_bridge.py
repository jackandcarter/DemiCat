import pytest

import sys
import types
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "demibot"
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

if "structlog" not in sys.modules:
    def _callable_stub(*args, **kwargs):
        return None

    def _factory_stub(*args, **kwargs):
        return _callable_stub

    sys.modules["structlog"] = types.SimpleNamespace(
        processors=types.SimpleNamespace(
            TimeStamper=lambda **kwargs: _callable_stub,
            add_log_level=_callable_stub,
            EventRenamer=lambda *args, **kwargs: _callable_stub,
            JSONRenderer=lambda *args, **kwargs: _callable_stub,
        ),
        make_filtering_bound_logger=lambda *args, **kwargs: _callable_stub,
        stdlib=types.SimpleNamespace(LoggerFactory=_factory_stub),
        configure=_callable_stub,
        get_logger=lambda *args, **kwargs: types.SimpleNamespace(
            info=_callable_stub,
            warning=_callable_stub,
            exception=_callable_stub,
            debug=_callable_stub,
        ),
    )

if "discord" not in sys.modules:
    class _DummyHTTPException(Exception):
        pass

    class _DummyWebhook:
        @classmethod
        def from_url(cls, *args, **kwargs):
            return cls()

        async def send(self, *args, **kwargs):
            return types.SimpleNamespace()

    class _DummyFile:
        def __init__(self, *args, **kwargs):
            pass

    class _DummyColor:
        def __init__(self, *args, **kwargs):
            self.value = args[0] if args else None

    class _DummyEmbed:
        def __init__(self, *args, **kwargs):
            self._data = {}
            description = kwargs.get("description")
            if description is not None:
                self._data["description"] = description
            timestamp = kwargs.get("timestamp")
            if timestamp is not None:
                self._data["timestamp"] = timestamp
            color = kwargs.get("color") or kwargs.get("colour")
            if color is not None:
                value = getattr(color, "value", color)
                self._data["color"] = value

        def set_footer(self, **kwargs):
            self._data.setdefault("footer", {}).update(kwargs)

        def set_author(self, **kwargs):
            self._data.setdefault("author", {}).update(kwargs)

        def set_image(self, **kwargs):
            self._data.setdefault("image", {}).update(kwargs)

        def to_dict(self):
            return self._data.copy()

    discord_module = types.ModuleType("discord")
    discord_module.HTTPException = _DummyHTTPException
    discord_module.Webhook = _DummyWebhook
    discord_module.File = _DummyFile
    discord_module.Color = _DummyColor
    discord_module.Embed = _DummyEmbed
    discord_module.abc = types.SimpleNamespace(Messageable=object)
    discord_module.errors = types.SimpleNamespace(Forbidden=_DummyHTTPException)
    sys.modules["discord"] = discord_module

if "sqlalchemy" not in sys.modules:
    def _select_stub(*args, **kwargs):
        return None

    sqlalchemy_module = types.ModuleType("sqlalchemy")
    sqlalchemy_module.select = _select_stub
    sys.modules["sqlalchemy"] = sqlalchemy_module

if "sqlalchemy.orm" not in sys.modules:
    sqlalchemy_orm_module = types.ModuleType("sqlalchemy.orm")
    sqlalchemy_orm_module.DeclarativeBase = type("DeclarativeBase", (), {})
    sys.modules["sqlalchemy.orm"] = sqlalchemy_orm_module

if "demibot.db.models" not in sys.modules:
    from dataclasses import dataclass

    class ChannelKind:
        OFFICER_CHAT = "officer"
        FC_CHAT = "fc"

    @dataclass
    class Membership:
        id: int
        guild_id: int
        user_id: int
        nickname: str
        avatar_url: str | None = None

    @dataclass
    class User:
        id: int
        discord_user_id: int
        global_name: str | None = None
        character_name: str | None = None
        world: str | None = None

    @dataclass
    class GuildChannel:
        channel_id: int | None = None
        guild_id: int | None = None
        kind: str | None = None

    @dataclass
    class Guild:
        id: int | None = None

    sys.modules["demibot.db.models"] = types.SimpleNamespace(
        ChannelKind=ChannelKind,
        Membership=Membership,
        User=User,
        GuildChannel=GuildChannel,
        Guild=Guild,
    )


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

    expected_header = "Message Sent by: Nick\n---"
    assert discord_content == ""
    assert nonce
    assert len(embeds) == 1

    embed_dict = embeds[0].to_dict()
    footer_text = embed_dict.get("footer", {}).get("text")
    assert footer_text and footer_text.endswith(f"{BRIDGE_MARKER}{nonce}")
    description = embed_dict.get("description", "")
    assert description == f"{expected_header}\n{content}"
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


@pytest.mark.parametrize(
    "provider_path",
    [
        pytest.param(("provider", "name"), id="nested-provider"),
        pytest.param(("providerName",), id="camel-provider"),
        pytest.param(("provider_name",), id="snake-provider"),
    ],
)
def test_extract_bridge_nonce_from_provider_fields(provider_path):
    nonce = "abc123"
    embed: dict[str, object] = {}

    if provider_path[0] == "provider":
        embed["provider"] = {provider_path[1]: f"Source • {BRIDGE_MARKER}{nonce}"}
    else:
        embed[provider_path[0]] = f"Source • {BRIDGE_MARKER}{nonce}"

    payload = {"embeds": [embed]}

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

    expected_header = "Message Sent by: Nick\n---"
    assert discord_content
    assert not uploads
    assert nonce
    assert len(embeds) >= 2

    first_description = embeds[0].to_dict().get("description", "")
    assert first_description.startswith(expected_header)

    embed_descriptions = [
        embed.to_dict().get("description", "") for embed in embeds
    ]
    combined = "".join(embed_descriptions)
    expected_prefix = f"{expected_header}\n"
    assert combined.startswith(expected_prefix)
    body_in_embeds = combined[len(expected_prefix):]

    total_length = 0
    for embed in embeds:
        data = embed.to_dict()
        description = data.get("description", "")
        assert len(description) <= 4096
        total_length += len(description)
        assert data.get("footer", {}).get("text", "").endswith(f"{BRIDGE_MARKER}{nonce}")

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

    expected_header = "Message Sent by: Nick / Hero / Eorzea\n---"
    assert discord_content == ""
    assert (
        embeds[0].to_dict().get("description", "")
        == f"{expected_header}\nHi"
    )
    assert not uploads
    assert nonce


def test_build_bridge_message_preserves_existing_header(sample_user, sample_membership):
    existing_header = "Message Sent by: Nick\n---"
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

