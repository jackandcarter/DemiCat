import sys
from pathlib import Path
import types
import asyncio
from types import SimpleNamespace

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

from demibot.db.models import Guild
from demibot.db.session import init_db, get_session
from demibot.http.routes.signup_presets import (
    list_signup_presets,
    create_signup_preset,
    delete_signup_preset,
    SignupPresetBody,
)


async def _run_test() -> None:
    db_path = Path("test_signup_presets.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        db.add(guild)
        await db.commit()
        ctx = SimpleNamespace(guild=guild)
        body = SignupPresetBody(
            name="Raid",
            buttons=[{"tag": "yes", "label": "Yes", "emoji": "✅", "style": 3, "maxSignups": 5}],
        )
        res = await create_signup_preset(body=body, ctx=ctx, db=db)
        pid = res["id"]
        presets = await list_signup_presets(ctx=ctx, db=db)
        assert presets == [
            {
                "id": pid,
                "name": "Raid",
                "buttons": [
                    {
                        "tag": "yes",
                        "label": "Yes",
                        "emoji": "✅",
                        "style": 3,
                        "maxSignups": 5,
                    }
                ],
            }
        ]
        await delete_signup_preset(preset_id=pid, ctx=ctx, db=db)
        presets = await list_signup_presets(ctx=ctx, db=db)
        assert presets == []
        break


def test_signup_presets() -> None:
    asyncio.run(_run_test())
