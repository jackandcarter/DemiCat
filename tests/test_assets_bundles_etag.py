import time
import asyncio
from datetime import datetime

from fastapi import FastAPI
from fastapi.testclient import TestClient
from sqlalchemy import select

from demibot.http.routes.assets import router as assets_router
from demibot.http.routes.bundles import router as bundles_router
from demibot.http.deps import RequestContext, api_key_auth
from demibot.db.session import init_db, get_session
from demibot.db.models import (
    User,
    Fc,
    FcUser,
    Asset,
    AssetDependency,
    AssetKind,
    IndexCheckpoint,
    AppearanceBundle,
    AppearanceBundleItem,
)


def _override_auth(user):
    def _auth_override():
        return RequestContext(user=user, guild=None, key=None, roles=[])

    return _auth_override


def _get_last_pull(user_id=1, fc_id=1):
    async def _run():
        async with get_session() as db:
            res = await db.execute(
                select(FcUser.last_pull_at).where(
                    FcUser.fc_id == fc_id, FcUser.user_id == user_id
                )
            )
            return res.scalar_one()

    return asyncio.run(_run())


def test_assets_etag_and_last_pull():
    user = User(id=1, discord_user_id=1, global_name="Alice")
    fc = Fc(id=1, name="FC", world="World")
    fcu = FcUser(fc_id=1, user_id=1, joined_at=datetime.utcnow())
    dep_asset = Asset(
        id=2,
        fc_id=1,
        kind=AssetKind.FILE,
        name="dep",
        hash="h2",
        size=1,
        uploader_id=1,
    )
    asset = Asset(
        id=1,
        fc_id=1,
        kind=AssetKind.FILE,
        name="file",
        hash="h1",
        size=1,
        uploader_id=1,
        created_at=datetime(2024, 1, 1),
    )
    dep_rel = AssetDependency(asset_id=1, dependency_id=2)
    cp_time = datetime(2024, 1, 1)
    cp = IndexCheckpoint(
        id=1, kind=AssetKind.FILE, last_id=1, last_generated_at=cp_time
    )

    async def _setup():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            db.add_all([user, fc, fcu, dep_asset, asset, dep_rel, cp])
            await db.commit()
            break

    asyncio.run(_setup())

    app = FastAPI()
    app.include_router(assets_router)
    app.dependency_overrides[api_key_auth] = _override_auth(user)
    client = TestClient(app)

    resp = client.get("/api/fc/1/assets")
    assert resp.status_code == 200
    data = resp.json()["items"]
    item = next(i for i in data if i["id"] == "1")
    assert item["uploader"] == "Alice"
    assert item["dependencies"] == ["2"]
    assert item["created_at"] is not None
    etag = resp.headers["ETag"]
    assert etag == cp_time.isoformat()
    lp1 = _get_last_pull()
    assert lp1 is not None
    time.sleep(0.01)
    resp2 = client.get("/api/fc/1/assets", headers={"If-None-Match": etag})
    assert resp2.status_code == 304
    lp2 = _get_last_pull()
    assert lp2 > lp1


def test_bundles_etag_and_last_pull():
    user = User(id=1, discord_user_id=1)
    fc = Fc(id=1, name="FC", world="World")
    fcu = FcUser(fc_id=1, user_id=1, joined_at=datetime.utcnow())
    asset = Asset(
        id=1,
        fc_id=1,
        kind=AssetKind.APPEARANCE,
        name="a",
        hash="h1",
        size=1,
    )
    bundle = AppearanceBundle(id=1, fc_id=1, name="b")
    item = AppearanceBundleItem(bundle_id=1, asset_id=1, quantity=1)
    cp_time = datetime(2024, 1, 1)
    cp = IndexCheckpoint(
        id=1, kind=AssetKind.APPEARANCE, last_id=1, last_generated_at=cp_time
    )

    async def _setup():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            db.add_all([user, fc, fcu, asset, bundle, item, cp])
            await db.commit()
            break

    asyncio.run(_setup())

    app = FastAPI()
    app.include_router(bundles_router)
    app.dependency_overrides[api_key_auth] = _override_auth(user)
    client = TestClient(app)

    resp = client.get("/api/fc/1/bundles")
    assert resp.status_code == 200
    etag = resp.headers["ETag"]
    assert etag == cp_time.isoformat()
    lp1 = _get_last_pull()
    assert lp1 is not None
    time.sleep(0.01)
    resp2 = client.get("/api/fc/1/bundles", headers={"If-None-Match": etag})
    assert resp2.status_code == 304
    lp2 = _get_last_pull()
    assert lp2 > lp1
