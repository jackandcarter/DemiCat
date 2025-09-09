import sys
from pathlib import Path
import asyncio

root = Path(__file__).resolve().parents[1] / 'demibot'
sys.path.append(str(root))

from demibot.http.routes import emojis


def test_get_unicode_emojis():
    res = asyncio.run(emojis.get_unicode_emojis())
    assert isinstance(res, list)
    assert any(e.get('emoji') == '😀' for e in res)
