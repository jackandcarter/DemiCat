import sys
from pathlib import Path
import types
import pytest
from fastapi import HTTPException

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.http.validation import validate_embed_payload
from demibot.http.schemas import EmbedDto, EmbedButtonDto


def test_title_too_long():
    dto = EmbedDto(id="1", title="x" * 257, description="d")
    with pytest.raises(HTTPException) as exc:
        validate_embed_payload(dto, [])
    assert exc.value.status_code == 422


def test_button_limit():
    dto = EmbedDto(id="1", title="t", description="d")
    buttons = [EmbedButtonDto(label=f"b{i}", custom_id=str(i)) for i in range(26)]
    with pytest.raises(HTTPException):
        validate_embed_payload(dto, buttons)


def test_button_width_limit():
    dto = EmbedDto(id="1", title="t", description="d")
    buttons = [EmbedButtonDto(label="b", custom_id="1", width=6)]
    with pytest.raises(HTTPException):
        validate_embed_payload(dto, buttons)


def test_button_total_width_limit():
    dto = EmbedDto(id="1", title="t", description="d")
    buttons = [
        EmbedButtonDto(label="b1", custom_id="1", width=25),
        EmbedButtonDto(label="b2", custom_id="2")
    ]
    with pytest.raises(HTTPException):
        validate_embed_payload(dto, buttons)
