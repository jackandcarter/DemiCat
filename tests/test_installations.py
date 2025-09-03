import asyncio
from datetime import datetime

from sqlalchemy import select

from demibot.db.models import (
    Guild,
    User,
    Asset,
    AssetKind,
    InstallStatus,
    UserInstallation,
)
from demibot.db.session import init_db, get_session
from demibot.http.deps import RequestContext
from demibot.http.routes.installations import (
    get_my_installations,
    post_my_installations,
    InstallationPayload,
)


def test_installations_flow():
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            guild = Guild(id=1, discord_guild_id=1, name="Guild")
            asset = Asset(id=1, kind=AssetKind.FILE, name="A", hash="h", size=1)
            db.add_all([user, guild, asset])
            await db.commit()

            ctx = RequestContext(user=user, guild=guild, key=object(), roles=[])

            # initial list empty
            items = await get_my_installations(ctx=ctx, db=db)
            assert items == []

            # download
            payload = InstallationPayload(assetId=1, status=InstallStatus.DOWNLOADED)
            await post_my_installations(payload, ctx=ctx, db=db)
            items = await get_my_installations(ctx=ctx, db=db)
            assert items[0]["status"] == "DOWNLOADED"

            # install
            payload = InstallationPayload(assetId=1, status=InstallStatus.INSTALLED)
            await post_my_installations(payload, ctx=ctx, db=db)
            items = await get_my_installations(ctx=ctx, db=db)
            assert items[0]["status"] == "INSTALLED"

            # apply
            payload = InstallationPayload(assetId=1, status=InstallStatus.APPLIED)
            await post_my_installations(payload, ctx=ctx, db=db)
            items = await get_my_installations(ctx=ctx, db=db)
            assert items[0]["status"] == "APPLIED"

            # only one row exists
            rows = (await db.execute(select(UserInstallation))).scalars().all()
            assert len(rows) == 1
            break
    asyncio.run(_run())
