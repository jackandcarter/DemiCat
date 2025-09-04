import asyncio
from pathlib import Path
import sys
import types

import pytest
from sqlalchemy.exc import StatementError

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

from demibot.db.models import Guild, GuildChannel, ChannelKind
from demibot.db.session import init_db, get_session


def test_enum_rejects_invalid_value():
    with pytest.raises(ValueError):
        ChannelKind("bogus")


def test_db_rejects_invalid_kind(tmp_path):
    db_path = tmp_path / "chan_kind.db"
    url = f"sqlite+aiosqlite:///{db_path}"
    asyncio.run(init_db(url))

    async def attempt_insert():
        async with get_session() as db:
            guild = Guild(id=1, discord_guild_id=1, name="Test")
            db.add(guild)
            db.add(GuildChannel(guild_id=1, channel_id=1, kind="bogus"))
            await db.flush()

    with pytest.raises((ValueError, StatementError)):
        asyncio.run(attempt_insert())
