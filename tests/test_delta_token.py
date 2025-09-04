import asyncio
from datetime import datetime, timedelta

from demibot.db.models import Guild, User, Fc, FcUser
from demibot.db.session import init_db, get_session
from demibot.http.deps import RequestContext
from demibot.http.routes.delta_token import get_delta_token
from sqlalchemy import select


def test_delta_token_updates_last_pull():
    async def _run():
        await init_db("sqlite+aiosqlite://")
        async with get_session() as db:
            user = User(id=1, discord_user_id=1, global_name="Test")
            guild = Guild(id=1, discord_guild_id=1, name="Guild")
            fc = Fc(id=1, name="FC", world="World")
            fcu = FcUser(fc_id=1, user_id=1, joined_at=datetime.utcnow())
            db.add_all([user, guild, fc, fcu])
            await db.commit()
            ctx = RequestContext(user=user, guild=guild, key=object(), roles=[])
            res = await get_delta_token(ctx=ctx, db=db)
            row = (
                await db.execute(select(FcUser).where(FcUser.user_id == user.id))
            ).scalar_one()
            since_dt = datetime.fromisoformat(res["since"])
            assert row.last_pull_at is not None
            assert abs(row.last_pull_at - since_dt) < timedelta(seconds=1)
    asyncio.run(_run())
