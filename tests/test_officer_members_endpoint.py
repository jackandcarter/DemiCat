import sys
from datetime import datetime
from pathlib import Path
from types import SimpleNamespace

import pytest
from fastapi import FastAPI
from httpx import ASGITransport, AsyncClient
from sqlalchemy import select, text

root = Path(__file__).resolve().parents[1] / "demibot"
if str(root) not in sys.path:
    sys.path.append(str(root))

from demibot.db.models import Guild, GuildMemberBan, Membership, User, UserKey
from demibot.db.session import get_session, init_db
from demibot.http.deps import RequestContext, api_key_auth, get_db
from demibot.http.routes.officer_members import router as officer_router


async def prepare_db() -> None:
    await init_db("sqlite+aiosqlite://")
    async with get_session() as db:
        for table in ("guild_member_ban", "user_keys", "memberships", "users", "guilds"):
            await db.execute(text(f"DELETE FROM {table}"))
        await db.commit()


def create_test_app() -> FastAPI:
    app = FastAPI()
    app.include_router(officer_router)
    return app


@pytest.mark.asyncio
async def test_officer_members_requires_officer_role():
    await prepare_db()
    app = create_test_app()

    user_ctx = SimpleNamespace(id=1)
    guild_ctx = SimpleNamespace(id=1, discord_guild_id=123)

    async def override_auth():
        return RequestContext(user=user_ctx, guild=guild_ctx, key=None, roles=[])

    async def override_db():
        yield None

    app.dependency_overrides[api_key_auth] = override_auth
    app.dependency_overrides[get_db] = override_db

    transport = ASGITransport(app=app)
    async with AsyncClient(transport=transport, base_url="http://test") as client:
        resp = await client.get("/api/officer/members")

    app.dependency_overrides.clear()
    assert resp.status_code == 403


@pytest.mark.asyncio
async def test_officer_members_lists_active_users():
    await prepare_db()

    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=999, name="Guild")
        user = User(id=1, discord_user_id=1001, global_name="Officer One", character_name="Alice")
        membership = Membership(guild_id=guild.id, user_id=user.id, nickname="Alicia")
        active_key = UserKey(
            id=1,
            user_id=user.id,
            guild_id=guild.id,
            token="abc",
            enabled=True,
            last_used_at=datetime(2024, 1, 5, 12, 0, 0),
        )
        stale_key = UserKey(
            id=2,
            user_id=user.id,
            guild_id=guild.id,
            token="def",
            enabled=True,
            last_used_at=datetime(2024, 1, 1, 12, 0, 0),
        )
        disabled_user = User(id=2, discord_user_id=2002, global_name="Member Two")
        disabled_membership = Membership(guild_id=guild.id, user_id=disabled_user.id)
        disabled_key = UserKey(
            id=3,
            user_id=disabled_user.id,
            guild_id=guild.id,
            token="ghi",
            enabled=False,
        )

        db.add_all(
            [
                guild,
                user,
                membership,
                active_key,
                stale_key,
                disabled_user,
                disabled_membership,
                disabled_key,
            ]
        )
        await db.commit()

    app = create_test_app()

    user_ctx = SimpleNamespace(id=1, discord_user_id=1001)
    guild_ctx = SimpleNamespace(id=1, discord_guild_id=999)

    async def override_auth():
        return RequestContext(user=user_ctx, guild=guild_ctx, key=None, roles=["officer"])

    async def override_db():
        async with get_session() as session:
            yield session

    app.dependency_overrides[api_key_auth] = override_auth
    app.dependency_overrides[get_db] = override_db

    transport = ASGITransport(app=app)
    async with AsyncClient(transport=transport, base_url="http://test") as client:
        resp = await client.get("/api/officer/members")

    app.dependency_overrides.clear()

    assert resp.status_code == 200
    data = resp.json()
    assert len(data) == 1
    entry = data[0]
    assert entry["discordUserId"] == "1001"
    assert entry["displayName"] == "Alicia"
    assert entry["globalName"] == "Officer One"
    assert entry["characterName"] == "Alice"
    assert entry["nickname"] == "Alicia"
    assert entry["isBanned"] is False
    assert entry["lastUsedAt"].startswith("2024-01-05T12:00:00")


@pytest.mark.asyncio
async def test_remove_member_disables_keys():
    await prepare_db()

    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=777, name="Guild")
        user = User(id=1, discord_user_id=1111, global_name="Member")
        membership = Membership(guild_id=guild.id, user_id=user.id)
        key = UserKey(id=1, user_id=user.id, guild_id=guild.id, token="tok", enabled=True)
        db.add_all([guild, user, membership, key])
        await db.commit()

    app = create_test_app()

    user_ctx = SimpleNamespace(id=1, discord_user_id=1111)
    guild_ctx = SimpleNamespace(id=1, discord_guild_id=777)

    async def override_auth():
        return RequestContext(user=user_ctx, guild=guild_ctx, key=None, roles=["officer"])

    async def override_db():
        async with get_session() as session:
            yield session

    app.dependency_overrides[api_key_auth] = override_auth
    app.dependency_overrides[get_db] = override_db

    transport = ASGITransport(app=app)
    async with AsyncClient(transport=transport, base_url="http://test") as client:
        resp = await client.post("/api/officer/members/1111/remove")

    app.dependency_overrides.clear()

    assert resp.status_code == 200

    async with get_session() as db:
        stored_key = await db.scalar(select(UserKey).where(UserKey.id == 1))
        assert stored_key is not None and stored_key.enabled is False


@pytest.mark.asyncio
async def test_ban_member_creates_record_and_disables_access():
    await prepare_db()

    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=555, name="Guild")
        user = User(id=1, discord_user_id=3333, global_name="Member")
        membership = Membership(guild_id=guild.id, user_id=user.id)
        key = UserKey(id=1, user_id=user.id, guild_id=guild.id, token="tok", enabled=True)
        db.add_all([guild, user, membership, key])
        await db.commit()

    app = create_test_app()

    user_ctx = SimpleNamespace(id=1, discord_user_id=3333)
    guild_ctx = SimpleNamespace(id=1, discord_guild_id=555)

    async def override_auth():
        return RequestContext(user=user_ctx, guild=guild_ctx, key=None, roles=["officer"])

    async def override_db():
        async with get_session() as session:
            yield session

    app.dependency_overrides[api_key_auth] = override_auth
    app.dependency_overrides[get_db] = override_db

    transport = ASGITransport(app=app)
    async with AsyncClient(transport=transport, base_url="http://test") as client:
        resp = await client.post("/api/officer/members/3333/ban")

    app.dependency_overrides.clear()

    assert resp.status_code == 200

    async with get_session() as db:
        stored_key = await db.scalar(select(UserKey).where(UserKey.id == 1))
        assert stored_key is not None and stored_key.enabled is False

        ban = await db.scalar(select(GuildMemberBan).where(GuildMemberBan.guild_id == 1))
        assert ban is not None
