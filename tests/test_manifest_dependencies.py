import asyncio
import hashlib
import io
import json
import zipfile
from datetime import datetime
from types import SimpleNamespace
import sys
from pathlib import Path

from sqlalchemy import select

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

from demibot.discordbot.cogs.vault import Vault
from demibot.db.models import Asset, AssetDependency, AssetKind, Fc, FcUser, User
from demibot.db.session import get_session, init_db


def test_manifest_declared_dependencies():
    dep_hash = "hdep"
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        zf.writestr(
            "manifest.json",
            json.dumps({"name": "bundle", "assets": [{"dependencies": [dep_hash]}]}),
        )
    data = buf.getvalue()
    main_hash = hashlib.sha256(data).hexdigest()

    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            user = User(id=1, discord_user_id=1)
            fc = Fc(id=1, name="FC", world="World")
            fcu = FcUser(fc_id=1, user_id=1, joined_at=datetime.utcnow())
            dep_asset = Asset(
                id=1,
                fc_id=1,
                kind=AssetKind.FILE,
                name="dep",
                hash=dep_hash,
                size=1,
                uploader_id=1,
            )
            db.add_all([user, fc, fcu, dep_asset])
            await db.commit()
            bot = SimpleNamespace(
                cfg=SimpleNamespace(database=SimpleNamespace(url="sqlite+aiosqlite://"))
            )
            vault = Vault(bot)
            asset = await vault._upsert_asset(
                db,
                1,
                AssetKind.APPEARANCE,
                "bundle",
                main_hash,
                len(data),
                1,
                datetime.utcnow(),
                [],
            )
            await vault._ensure_bundle(db, asset, data)
            await db.commit()
            res = await db.execute(
                select(AssetDependency.dependency_id).where(
                    AssetDependency.asset_id == asset.id
                )
            )
            assert res.scalar_one() == dep_asset.id
            break

    asyncio.run(_run())
