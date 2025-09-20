import json
import sys
import types
from pathlib import Path
from datetime import datetime, timezone

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.http.routes.events import CreateEventBody, FieldBody, EmbedButtonDto, ButtonStyle, format_event_embed

FIXTURE = json.loads((Path(__file__).parent / "data" / "event_preview_samples.json").read_text())
TIMESTAMP = datetime(2024, 4, 1, 20, 0, tzinfo=timezone.utc)


def _embed_to_dict(embed):
    return embed.to_dict()


def _buttons_to_list(buttons):
    return [b.model_dump(mode="json", by_alias=True, exclude_none=True) for b in buttons]


def _build_event_body():
    return CreateEventBody(
        channelId="123",
        title="Raid Night",
        description="Clear the raid together.",
        time=TIMESTAMP,
        url="https://example.com/event",
        imageUrl="https://example.com/image.png",
        thumbnailUrl="https://example.com/thumb.png",
        color=0x123456,
        fields=[
            FieldBody(name="When", value="Tonight 8pm", inline=False),
            FieldBody(name="Where", value="Discord", inline=True),
        ],
        buttons=[
            EmbedButtonDto(label="Sign Up", custom_id="rsvp:yes", style=ButtonStyle.primary, width=80, row_index=0),
            EmbedButtonDto(label="Info", url="https://example.com/info", style=ButtonStyle.link, width=56, row_index=0),
        ],
    )


def _build_template_body():
    return CreateEventBody(
        channelId="456",
        title="Static Meeting",
        description="Discuss strategy.",
        time=TIMESTAMP,
        url="https://example.com/template",
        imageUrl="https://example.com/template-image.png",
        thumbnailUrl="https://example.com/template-thumb.png",
        color=0xAA55CC,
        fields=[
            FieldBody(name="Agenda", value="Discuss plan", inline=False),
            FieldBody(name="Duration", value="60 min", inline=True),
        ],
        buttons=[
            EmbedButtonDto(label="DPS Signup", custom_id="rsvp:dps", style=ButtonStyle.primary, width=112, row_index=0),
            EmbedButtonDto(label="Healer Signup", custom_id="rsvp:heals", style=ButtonStyle.success, width=128, row_index=0),
            EmbedButtonDto(label="Tank Signup", custom_id="rsvp:tanks", style=ButtonStyle.secondary, width=104, row_index=1),
            EmbedButtonDto(label="Guide", url="https://example.com/guide", style=ButtonStyle.link, width=64, row_index=1),
        ],
    )


def test_event_helper_fc_matches_fixture():
    body = _build_event_body()
    formatted = format_event_embed(body, timestamp=TIMESTAMP, mention_ids=[12345])
    expected = FIXTURE["event_fc"]
    assert formatted.content == expected["content"]
    assert _embed_to_dict(formatted.embed) == expected["embed"]
    assert _buttons_to_list(formatted.buttons) == expected["buttons"]


def test_event_helper_officer_matches_fixture():
    body = _build_event_body()
    formatted = format_event_embed(body, timestamp=TIMESTAMP, mention_ids=[12345, 67890])
    expected = FIXTURE["event_officer"]
    assert formatted.content == expected["content"]
    assert _embed_to_dict(formatted.embed) == expected["embed"]
    assert _buttons_to_list(formatted.buttons) == expected["buttons"]


def test_template_helper_fc_matches_fixture():
    body = _build_template_body()
    formatted = format_event_embed(body, timestamp=TIMESTAMP, mention_ids=[12345])
    expected = FIXTURE["template_fc"]
    assert formatted.content == expected["content"]
    assert _embed_to_dict(formatted.embed) == expected["embed"]
    assert _buttons_to_list(formatted.buttons) == expected["buttons"]


def test_template_helper_officer_matches_fixture():
    body = _build_template_body()
    formatted = format_event_embed(body, timestamp=TIMESTAMP, mention_ids=[12345, 67890])
    expected = FIXTURE["template_officer"]
    assert formatted.content == expected["content"]
    assert _embed_to_dict(formatted.embed) == expected["embed"]
    assert _buttons_to_list(formatted.buttons) == expected["buttons"]
