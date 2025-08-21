import asyncio
import json
from pathlib import Path

from sqlalchemy import select

from demibot.db.models import Guild, GuildChannel
from demibot.db.session import init_db, get_session
from demibot.http.deps import RequestContext
from demibot.http.routes import channels as channel_routes


def _setup_db(path: str) -> None:
    db_path = Path(path)
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    asyncio.run(init_db(url))

    async def populate() -> None:
        async for db in get_session():
            guild = Guild(id=1, discord_guild_id=1, name="Test")
            db.add(guild)
            db.add(
                GuildChannel(
                    guild_id=guild.id, channel_id=100, kind="event", name="12345"
                )
            )
            await db.commit()
            break

    asyncio.run(populate())


def test_get_channels_returns_placeholder_and_flags_retry(monkeypatch):
    _setup_db("test_channels.db")

    async def fake_ensure_channel_name(*args, **kwargs):
        return None

    monkeypatch.setattr(channel_routes, "ensure_channel_name", fake_ensure_channel_name)

    async def dummy_broadcast(*args, **kwargs):
        return None

    monkeypatch.setattr(channel_routes.manager, "broadcast_text", dummy_broadcast)

    class Dummy:
        pass

    async def run():
        async for db in get_session():
            guild = (await db.execute(select(Guild).where(Guild.id == 1))).scalar_one()
            ctx = RequestContext(user=Dummy(), guild=guild, key=Dummy(), roles=[])
            resp = await channel_routes.get_channels(ctx=ctx, db=db)
            data = json.loads(resp.body.decode())
            row = (
                await db.execute(
                    select(GuildChannel).where(
                        GuildChannel.guild_id == 1,
                        GuildChannel.channel_id == 100,
                        GuildChannel.kind == "event",
                    )
                )
            ).scalar_one()
            return data, row.name

    data, name = asyncio.run(run())
    assert data["event"][0]["name"] == "100"
    assert name is None

