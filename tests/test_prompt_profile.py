import types
import sys
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1] / "demibot"))
import demibot.config as config  # type: ignore


def get_prompt_profile():
    code = [c for c in config.ensure_config.__code__.co_consts if isinstance(c, types.CodeType) and c.co_name == '_prompt_profile'][0]
    return types.FunctionType(code, config.__dict__)


def test_prompt_profile_valid_port(monkeypatch):
    _prompt_profile = get_prompt_profile()
    profile = config.DBProfile()
    inputs = iter(["", "1234", ""])
    monkeypatch.setattr(config, "input", lambda _: next(inputs), raising=False)
    _prompt_profile("Test", profile)
    assert profile.port == 1234


def test_prompt_profile_invalid_then_valid_port(monkeypatch, capsys):
    _prompt_profile = get_prompt_profile()
    profile = config.DBProfile()
    inputs = iter(["", "abc", "2345", ""])
    monkeypatch.setattr(config, "input", lambda _: next(inputs), raising=False)
    _prompt_profile("Test", profile)
    assert profile.port == 2345
    captured = capsys.readouterr()
    assert "Invalid port" in captured.out


def test_prompt_profile_empty_port(monkeypatch):
    _prompt_profile = get_prompt_profile()
    profile = config.DBProfile()
    inputs = iter(["", "", ""])
    monkeypatch.setattr(config, "input", lambda _: next(inputs), raising=False)
    _prompt_profile("Test", profile)
    assert profile.port == 3306
