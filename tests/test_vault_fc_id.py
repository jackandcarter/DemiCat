import asyncio
from types import SimpleNamespace

from sqlalchemy import select

import io
import json
import zipfile
from datetime import datetime

from demibot.discordbot.cogs.vault import Vault
from demibot.db.models import AppearanceBundle, AssetKind, Fc, Guild, User
from demibot.db.session import get_session, init_db


def test_vault_assigns_fc_id():
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            guild = Guild(id=1, discord_guild_id=1, name="Guild")
            fc = Fc(id=1, name="FC", world="World")
            user = User(id=1, discord_user_id=1)
            db.add_all([guild, fc, user])
            await db.commit()
            bot = SimpleNamespace(
                cfg=SimpleNamespace(database=SimpleNamespace(url="sqlite+aiosqlite://"))
            )
            vault = Vault(bot)
            fc_id = await vault._get_fc_id(db, SimpleNamespace(id=1))
            assert fc_id == 1
            asset = await vault._upsert_asset(
                db,
                fc_id,
                AssetKind.APPEARANCE,
                "bundle",
                "hash",
                1,
                1,
                datetime.utcnow(),
                [],
            )
            assert asset.fc_id == 1
            buf = io.BytesIO()
            with zipfile.ZipFile(buf, "w") as zf:
                zf.writestr("manifest.json", json.dumps({"name": "bundle"}))
            await vault._ensure_bundle(db, asset, buf.getvalue())
            await db.commit()
            bundle = (await db.execute(select(AppearanceBundle))).scalar_one()
            assert bundle.fc_id == 1
            break

    asyncio.run(_run())

