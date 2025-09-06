import asyncio
from pathlib import Path
from sqlalchemy import text
import sys, types

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

from demibot.db.session import init_db, get_session


async def _run_test():
    db_path = Path("test_migration_index.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        idx_rows = await db.execute(text("PRAGMA index_list('event_signups')"))
        index_names = [r[1] for r in idx_rows]
        assert "ix_event_signups_discord_message_id_choice" in index_names


def test_event_signup_index_exists():
    asyncio.run(_run_test())
