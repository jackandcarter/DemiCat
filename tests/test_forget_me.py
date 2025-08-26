from datetime import datetime
import asyncio

from sqlalchemy import select

from demibot.db.models import (
    Guild,
    User,
    Fc,
    FcUser,
    Asset,
    AssetKind,
    UserInstallation,
    InstallStatus,
)
from demibot.db.session import init_db, get_session
from demibot.http.deps import RequestContext
from demibot.http.routes.user_settings import forget_me


def test_forget_me_scrubs_user_and_assets():
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async for db in get_session():
            user = User(
                id=1,
                discord_user_id=1,
                global_name="Test",
                discriminator="1234",
                character_name="Char",
                world="World",
            )
            guild = Guild(id=1, discord_guild_id=1, name="Guild")
            fc = Fc(id=1, name="FC", world="World")
            fcu = FcUser(fc_id=1, user_id=1, joined_at=datetime.utcnow())
            asset = Asset(
                id=1,
                kind=AssetKind.FILE,
                name="A",
                hash="h",
                size=1,
            )
            inst = UserInstallation(
                id=1,
                user_id=1,
                asset_id=1,
                status=InstallStatus.INSTALLED,
            )
            db.add_all([user, guild, fc, fcu, asset, inst])
            await db.commit()
            ctx = RequestContext(user=user, guild=guild, key=object(), roles=[])
            await forget_me(ctx=ctx, db=db)
            user_row = await db.get(User, 1)
            assert user_row.global_name is None
            assert user_row.character_name is None
            assert user_row.world is None
            inst_rows = (await db.execute(select(UserInstallation))).all()
            assert not inst_rows
            asset_row = (
                await db.execute(select(Asset).where(Asset.id == 1))
            ).scalar_one()
            assert asset_row.deleted_at is not None
            break
    asyncio.run(_run())
