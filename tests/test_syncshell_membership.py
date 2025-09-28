import asyncio

import pytest

pytest.importorskip("sqlalchemy")

from demibot.db.session import init_db, get_session
import demibot.db.session as db_session
from demibot.db.models import User
from demibot.http.deps import RequestContext
from demibot.http.routes import syncshell


async def _prepare_db():
    db_session._engine = None
    db_session._Session = None
    await init_db("sqlite+aiosqlite://")
    return get_session()


def _make_ctx(user: User) -> RequestContext:
    return RequestContext(user=user, guild=None, key=object(), roles=[])


def test_syncshell_invite_acceptance_and_members():
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user_a = User(id=1, discord_user_id=10, global_name="Alpha")
            user_b = User(id=2, discord_user_id=20, global_name="Beta")
            db.add_all([user_a, user_b])
            await db.commit()

            ctx_a = _make_ctx(user_a)
            ctx_b = _make_ctx(user_b)

            await syncshell.pair(ctx=ctx_a, db=db)
            await syncshell.pair(ctx=ctx_b, db=db)

            overview_a_initial = await syncshell.list_memberships(ctx=ctx_a, db=db)
            assert overview_a_initial["members"] == []
            assert overview_a_initial["pendingApprovals"] == []

            invite = await syncshell.create_invite(
                payload=syncshell.InviteCreateRequest(member_id=user_b.id),
                ctx=ctx_a,
                db=db,
            )
            assert invite["status"] == "pending"

            overview_a_after_invite = await syncshell.list_memberships(ctx=ctx_a, db=db)
            assert overview_a_after_invite["invites"][0]["status"] == "pending"
            assert overview_a_after_invite["invites"][0]["direction"] == "outgoing"

            overview_b_pending = await syncshell.list_memberships(ctx=ctx_b, db=db)
            assert overview_b_pending["pendingApprovals"][0]["displayName"] == "Alpha"
            assert overview_b_pending["pendingApprovals"][0]["requestedAt"].endswith("Z")

            pending = await syncshell.list_pending(ctx=ctx_b, db=db)
            assert len(pending["pending"]) == 1
            assert pending["pending"][0]["id"] == invite["id"]

            await syncshell.accept_invite(invite["id"], ctx=ctx_b, db=db)

            invites_after = await syncshell.list_invites(ctx=ctx_a, db=db)
            assert invites_after["invites"][0]["status"] == "accepted"

            members_a = await syncshell.list_members(ctx=ctx_a, db=db)
            members_b = await syncshell.list_members(ctx=ctx_b, db=db)

            assert [m["id"] for m in members_a["members"]] == [str(user_b.id)]
            assert [m["id"] for m in members_b["members"]] == [str(user_a.id)]

            overview_a_final = await syncshell.list_memberships(ctx=ctx_a, db=db)
            assert overview_a_final["currentlySynced"] == []
            assert overview_a_final["pendingApprovals"] == []
            assert overview_a_final["members"][0]["id"] == str(user_b.id)
            assert overview_a_final["members"][0]["presence"] == "offline"
            assert overview_a_final["members"][0]["syncStatus"] is None
            assert overview_a_final["invites"][0]["status"] == "accepted"

            overview_b_final = await syncshell.list_memberships(ctx=ctx_b, db=db)
            assert overview_b_final["members"][0]["id"] == str(user_a.id)
            assert overview_b_final["members"][0]["presence"] == "offline"
            assert overview_b_final["pendingApprovals"] == []

            pending_after = await syncshell.list_pending(ctx=ctx_b, db=db)
            assert pending_after["pending"] == []

    asyncio.run(_run())


def test_syncshell_presence_updates():
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            user_a = User(id=11, discord_user_id=110, global_name="Gamma")
            user_b = User(id=12, discord_user_id=120, global_name="Delta")
            db.add_all([user_a, user_b])
            await db.commit()

            ctx_a = _make_ctx(user_a)
            ctx_b = _make_ctx(user_b)

            await syncshell.pair(ctx=ctx_a, db=db)
            await syncshell.pair(ctx=ctx_b, db=db)

            invite = await syncshell.create_invite(
                payload=syncshell.InviteCreateRequest(member_id=user_b.id),
                ctx=ctx_a,
                db=db,
            )
            await syncshell.accept_invite(invite["id"], ctx=ctx_b, db=db)

            await syncshell.update_presence(
                payload=syncshell.PresenceUpdateRequest(active_member_ids=[user_b.id]),
                ctx=ctx_a,
                db=db,
            )

            presence = await syncshell.get_presence(ctx=ctx_a, db=db)
            assert presence["currentlySynced"][0]["id"] == str(user_b.id)
            assert presence["presence"][0]["active"] is True

            overview_syncing = await syncshell.list_memberships(ctx=ctx_a, db=db)
            assert overview_syncing["currentlySynced"][0]["id"] == str(user_b.id)
            assert overview_syncing["currentlySynced"][0]["presence"] == "online"
            assert overview_syncing["currentlySynced"][0]["syncStatus"] == "syncing"
            assert overview_syncing["currentlySynced"][0]["syncedAt"].endswith("Z")
            assert overview_syncing["members"][0]["presence"] == "online"

            await syncshell.update_presence(
                payload=syncshell.PresenceUpdateRequest(active_member_ids=[]),
                ctx=ctx_a,
                db=db,
            )
            presence_after = await syncshell.get_presence(ctx=ctx_a, db=db)
            assert presence_after["presence"][0]["active"] is False

            overview_offline = await syncshell.list_memberships(ctx=ctx_a, db=db)
            assert overview_offline["currentlySynced"] == []
            assert overview_offline["members"][0]["presence"] == "offline"
            assert overview_offline["members"][0]["lastSeen"].endswith("Z")

    asyncio.run(_run())
