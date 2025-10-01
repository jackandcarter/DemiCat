import json
import sys
from pathlib import Path

import pytest
from sqlalchemy import insert
from sqlalchemy.ext.asyncio import AsyncSession, create_async_engine
from sqlalchemy.orm import sessionmaker

root = Path(__file__).resolve().parents[1] / 'demibot'
sys.path.append(str(root))

from demibot.db.base import Base
from demibot.db.models import UnicodeEmoji
from demibot.http.routes import emojis


@pytest.mark.asyncio
async def test_get_unicode_emojis():
    engine = create_async_engine("sqlite+aiosqlite:///:memory:")
    async with engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)
    dataset_path = root / "demibot" / "data" / "unicode_emojis.json"
    payload = json.loads(dataset_path.read_text(encoding="utf-8"))
    assert len(payload) > 1000

    Session = sessionmaker(engine, expire_on_commit=False, class_=AsyncSession)
    async with Session() as session:
        emojis._unicode_cache = None
        await session.execute(
            insert(UnicodeEmoji),
            [
                {"emoji": item["emoji"], "name": item["name"], "image_url": item["imageUrl"]}
                for item in payload
            ],
        )
        await session.commit()
        res = await emojis.get_unicode_emojis(session)
        assert isinstance(res, list)
        assert len(res) == len(payload)
        assert any(e.get("emoji") == "📢" for e in res)
