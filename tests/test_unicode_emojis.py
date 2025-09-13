import sys
from pathlib import Path
import pytest
from sqlalchemy.ext.asyncio import create_async_engine, AsyncSession
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
    Session = sessionmaker(engine, expire_on_commit=False, class_=AsyncSession)
    async with Session() as session:
        session.add(UnicodeEmoji(emoji="😀", name="grinning face", image_url="url"))
        await session.commit()
        res = await emojis.get_unicode_emojis(session)
        assert isinstance(res, list)
        assert any(e.get('emoji') == '😀' for e in res)
