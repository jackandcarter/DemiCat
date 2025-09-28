import asyncio
from datetime import datetime, timedelta

from demibot.db.models import (
    SyncshellInvite,
    SyncshellMember,
    SyncshellPairing,
    User,
)
from demibot.db.session import get_session, init_db
import demibot.db.session as db_session
from demibot.http.deps import RequestContext

from .syncshell_import import syncshell


async def _prepare_db():
    db_session._engine = None
    db_session._Session = None
    await init_db("sqlite+aiosqlite://")
    return get_session()


def _pairing(user_id: int, *, token: str) -> SyncshellPairing:
    return SyncshellPairing(
        user_id=user_id,
        token=token,
        expires_at=datetime.utcnow() + timedelta(hours=1),
    )


def test_create_invite_by_name_unique_match():
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            inviter = User(id=1, discord_user_id=1, global_name="Alpha")
            target = User(id=2, discord_user_id=2, global_name="Beta")
            db.add_all(
                [
                    inviter,
                    target,
                    _pairing(inviter.id, token="token-inviter"),
                    _pairing(target.id, token="token-target"),
                ]
            )
            await db.commit()

            ctx_inviter = RequestContext(user=inviter, guild=None, key=object(), roles=[])
            syncshell.RATE_LIMIT = 100

            response = await syncshell.create_invite(
                syncshell.InviteCreateRequest(member="  beta  "), ctx=ctx_inviter, db=db
            )

            invite = await db.get(SyncshellInvite, response["id"])
            assert invite is not None
            assert invite.target_user_id == target.id
            assert invite.target_display_name == "Beta"

    asyncio.run(_run())


def test_pending_invite_accept_flow_sets_memberships():
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            inviter = User(id=1, discord_user_id=1, global_name="Alpha")
            target_one = User(id=2, discord_user_id=2, character_name="Echo")
            target_two = User(id=3, discord_user_id=3, character_name="Echo")
            db.add_all(
                [
                    inviter,
                    target_one,
                    target_two,
                    _pairing(inviter.id, token="token-inviter"),
                    _pairing(target_one.id, token="token-target-1"),
                    _pairing(target_two.id, token="token-target-2"),
                ]
            )
            await db.commit()

            ctx_inviter = RequestContext(user=inviter, guild=None, key=object(), roles=[])
            ctx_target_one = RequestContext(
                user=target_one, guild=None, key=object(), roles=[]
            )
            ctx_target_two = RequestContext(
                user=target_two, guild=None, key=object(), roles=[]
            )
            syncshell.RATE_LIMIT = 100

            response = await syncshell.create_invite(
                syncshell.InviteCreateRequest(member="Echo"), ctx=ctx_inviter, db=db
            )
            invite = await db.get(SyncshellInvite, response["id"])
            assert invite is not None
            assert invite.target_user_id is None

            pending_for_one = await syncshell.list_pending(ctx=ctx_target_one, db=db)
            assert [entry["id"] for entry in pending_for_one["pending"]] == [invite.id]

            pending_for_two = await syncshell.list_pending(ctx=ctx_target_two, db=db)
            assert [entry["id"] for entry in pending_for_two["pending"]] == [invite.id]

            await syncshell.accept_invite(invite.id, ctx=ctx_target_one, db=db)
            await db.refresh(invite)

            assert invite.status == "accepted"
            assert invite.target_user_id == target_one.id
            assert invite.target_display_name == "Echo"

            members = await db.execute(syncshell.select(SyncshellMember))
            pairs = {(m.user_id, m.member_user_id) for m in members.scalars()}
            assert pairs == {(inviter.id, target_one.id), (target_one.id, inviter.id)}

            post_pending_two = await syncshell.list_pending(ctx=ctx_target_two, db=db)
            assert post_pending_two["pending"] == []

    asyncio.run(_run())


def test_create_invite_preserves_display_name_casing_for_ambiguous_match():
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            inviter = User(id=1, discord_user_id=1, global_name="Alpha")
            target_one = User(id=2, discord_user_id=2, character_name="Echo")
            target_two = User(id=3, discord_user_id=3, character_name="Echo")
            db.add_all(
                [
                    inviter,
                    target_one,
                    target_two,
                    _pairing(inviter.id, token="token-inviter"),
                    _pairing(target_one.id, token="token-target-1"),
                    _pairing(target_two.id, token="token-target-2"),
                ]
            )
            await db.commit()

            ctx_inviter = RequestContext(user=inviter, guild=None, key=object(), roles=[])
            syncshell.RATE_LIMIT = 100

            response = await syncshell.create_invite(
                syncshell.InviteCreateRequest(member="  eChO  "), ctx=ctx_inviter, db=db
            )

            invite = await db.get(SyncshellInvite, response["id"])
            assert invite is not None
            assert invite.target_user_id is None
            assert invite.target_display_name == "eChO"

    asyncio.run(_run())


def test_pending_invite_deny_sets_target_user():
    async def _run():
        session_factory = await _prepare_db()
        async with session_factory as db:
            inviter = User(id=1, discord_user_id=1, global_name="Alpha")
            target_one = User(id=2, discord_user_id=2, character_name="Echo")
            target_two = User(id=3, discord_user_id=3, character_name="Echo")
            db.add_all(
                [
                    inviter,
                    target_one,
                    target_two,
                    _pairing(inviter.id, token="token-inviter"),
                    _pairing(target_one.id, token="token-target-1"),
                    _pairing(target_two.id, token="token-target-2"),
                ]
            )
            await db.commit()

            ctx_inviter = RequestContext(user=inviter, guild=None, key=object(), roles=[])
            ctx_target_two = RequestContext(
                user=target_two, guild=None, key=object(), roles=[]
            )
            syncshell.RATE_LIMIT = 100

            response = await syncshell.create_invite(
                syncshell.InviteCreateRequest(member="Echo"), ctx=ctx_inviter, db=db
            )
            invite = await db.get(SyncshellInvite, response["id"])
            assert invite is not None

            pending_for_two = await syncshell.list_pending(ctx=ctx_target_two, db=db)
            assert [entry["id"] for entry in pending_for_two["pending"]] == [invite.id]

            await syncshell.deny_invite(invite.id, ctx=ctx_target_two, db=db)
            await db.refresh(invite)

            assert invite.status == "denied"
            assert invite.target_user_id == target_two.id
            assert invite.target_display_name == "Echo"

            members = await db.execute(syncshell.select(SyncshellMember))
            assert list(members.scalars()) == []

    asyncio.run(_run())

