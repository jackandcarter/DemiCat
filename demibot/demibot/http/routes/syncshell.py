from __future__ import annotations

import os
from uuid import uuid4
from typing import Any
from datetime import datetime, timedelta
import json

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import (
    SyncshellPairing,
    SyncshellManifest,
    SyncshellRateLimit,
    SyncshellInvite,
    SyncshellMember,
    SyncshellPresence,
    User,
)
from ..vault import presign_upload, presign_download


router = APIRouter(prefix="/api/syncshell", tags=["syncshell"])

RATE_LIMIT = int(os.getenv("SYNC_SHELL_MAX_REQUESTS_PER_MINUTE", "30"))
TOKEN_TTL = int(os.getenv("SYNC_SHELL_TOKEN_TTL", "300"))  # seconds
MAX_MANIFEST_BYTES = 1024 * 1024  # 1 MiB manifest payload cap


async def _check_rate_limit(user_id: int, db: AsyncSession) -> None:
    now = datetime.utcnow()
    record = await db.get(SyncshellRateLimit, user_id)
    if record and (now - record.window_start).total_seconds() < 60:
        if record.requests >= RATE_LIMIT:
            raise HTTPException(status_code=429, detail="rate limit exceeded")
        record.requests += 1
    else:
        if record:
            record.requests = 1
            record.window_start = now
        else:
            record = SyncshellRateLimit(user_id=user_id, requests=1, window_start=now)
            db.add(record)
    await db.commit()


def _display_name(user: User | None) -> str:
    if not user:
        return "Unknown"
    return user.global_name or user.character_name or str(user.id)


def _to_iso(dt: datetime | None) -> str | None:
    if not dt:
        return None
    return dt.replace(microsecond=0).isoformat() + "Z"


class InviteCreateRequest(BaseModel):
    member: str | None = Field(default=None, description="Display name of target")
    member_id: int | None = Field(default=None, description="Identifier of target user")


class PresenceUpdateRequest(BaseModel):
    active_member_ids: list[int] = Field(
        default_factory=list, description="Members currently nearby or active"
    )


async def _require_pairing(ctx: RequestContext, db: AsyncSession) -> None:
    pairing = await db.get(SyncshellPairing, ctx.user.id)
    if not pairing or pairing.expires_at < datetime.utcnow():
        raise HTTPException(status_code=401, detail="pairing token expired")


async def _ensure_membership(
    db: AsyncSession, user_id: int, member_user_id: int
) -> None:
    if user_id == member_user_id:
        return
    result = await db.execute(
        select(SyncshellMember).where(
            SyncshellMember.user_id == user_id,
            SyncshellMember.member_user_id == member_user_id,
        )
    )
    if result.scalars().first() is None:
        db.add(
            SyncshellMember(
                user_id=user_id,
                member_user_id=member_user_id,
                created_at=datetime.utcnow(),
            )
        )


async def _ensure_presence_record(
    db: AsyncSession, user_id: int, member_user_id: int
) -> None:
    if user_id == member_user_id:
        return
    result = await db.execute(
        select(SyncshellPresence).where(
            SyncshellPresence.user_id == user_id,
            SyncshellPresence.member_user_id == member_user_id,
        )
    )
    if result.scalars().first() is None:
        db.add(
            SyncshellPresence(
                user_id=user_id,
                member_user_id=member_user_id,
                active=False,
                last_seen=datetime.utcnow(),
            )
        )


def _serialize_invite(invite: SyncshellInvite, direction: str) -> dict[str, Any]:
    return {
        "id": invite.id,
        "target": invite.target_display_name,
        "status": invite.status,
        "updatedAt": _to_iso(invite.updated_at) or _to_iso(invite.created_at),
        "direction": direction,
    }


@router.get("/invites")
async def list_invites(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    result = await db.execute(
        select(SyncshellInvite)
        .where(SyncshellInvite.inviter_id == ctx.user.id)
        .order_by(SyncshellInvite.updated_at.desc())
    )
    invites = [_serialize_invite(invite, "outgoing") for invite in result.scalars().all()]
    return {"invites": invites}


@router.post("/invites")
async def create_invite(
    payload: InviteCreateRequest,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)

    target_user: User | None = None
    display_name = (payload.member or "").strip()
    if payload.member_id is not None:
        target_user = await db.get(User, payload.member_id)
        if not target_user:
            raise HTTPException(status_code=404, detail="member not found")
        display_name = _display_name(target_user)

    if not display_name:
        raise HTTPException(status_code=422, detail="member is required")

    if target_user and target_user.id == ctx.user.id:
        raise HTTPException(status_code=400, detail="cannot invite yourself")

    if target_user:
        existing_member = await db.execute(
            select(SyncshellMember).where(
                SyncshellMember.user_id == ctx.user.id,
                SyncshellMember.member_user_id == target_user.id,
            )
        )
        if existing_member.scalars().first() is not None:
            raise HTTPException(status_code=409, detail="already a member")

        existing_invite = await db.execute(
            select(SyncshellInvite).where(
                SyncshellInvite.inviter_id == ctx.user.id,
                SyncshellInvite.target_user_id == target_user.id,
                SyncshellInvite.status == "pending",
            )
        )
        invite = existing_invite.scalars().first()
        if invite is not None:
            return _serialize_invite(invite, "outgoing")
    else:
        existing_invite = await db.execute(
            select(SyncshellInvite).where(
                SyncshellInvite.inviter_id == ctx.user.id,
                SyncshellInvite.target_display_name == display_name,
                SyncshellInvite.status == "pending",
            )
        )
        invite = existing_invite.scalars().first()
        if invite is not None:
            return _serialize_invite(invite, "outgoing")

    invite = SyncshellInvite(
        id=uuid4().hex,
        inviter_id=ctx.user.id,
        target_user_id=target_user.id if target_user else None,
        target_display_name=display_name,
        status="pending",
        created_at=datetime.utcnow(),
        updated_at=datetime.utcnow(),
    )
    db.add(invite)
    await db.commit()
    await db.refresh(invite)
    return _serialize_invite(invite, "outgoing")


async def _get_invite_for_target(
    db: AsyncSession, invite_id: str, target_user_id: int
) -> SyncshellInvite:
    invite = await db.get(SyncshellInvite, invite_id)
    if not invite or invite.target_user_id != target_user_id:
        raise HTTPException(status_code=404, detail="invite not found")
    if invite.status != "pending":
        raise HTTPException(status_code=400, detail="invite already processed")
    return invite


async def _finalize_invite(
    db: AsyncSession, invite: SyncshellInvite, accepted: bool
) -> None:
    invite.status = "accepted" if accepted else "denied"
    invite.updated_at = datetime.utcnow()
    if accepted and invite.target_user_id:
        await _ensure_membership(db, invite.inviter_id, invite.target_user_id)
        await _ensure_membership(db, invite.target_user_id, invite.inviter_id)
        await _ensure_presence_record(db, invite.inviter_id, invite.target_user_id)
        await _ensure_presence_record(db, invite.target_user_id, invite.inviter_id)
    await db.commit()


@router.post("/invites/{invite_id}/accept")
async def accept_invite(
    invite_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    invite = await _get_invite_for_target(db, invite_id, ctx.user.id)
    await _finalize_invite(db, invite, accepted=True)
    return {"status": "accepted"}


@router.post("/invites/{invite_id}/deny")
async def deny_invite(
    invite_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    invite = await _get_invite_for_target(db, invite_id, ctx.user.id)
    await _finalize_invite(db, invite, accepted=False)
    return {"status": "denied"}


@router.get("/pending")
async def list_pending(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    result = await db.execute(
        select(SyncshellInvite)
        .where(
            SyncshellInvite.target_user_id == ctx.user.id,
            SyncshellInvite.status == "pending",
        )
        .order_by(SyncshellInvite.created_at.desc())
    )
    invites = result.scalars().all()
    pending: list[dict[str, Any]] = []
    for invite in invites:
        inviter = await db.get(User, invite.inviter_id)
        pending.append(
            {
                "id": invite.id,
                "requesterId": invite.inviter_id,
                "displayName": _display_name(inviter),
                "requestedAt": _to_iso(invite.created_at),
                "direction": "incoming",
            }
        )
    return {"pending": pending}


@router.get("/members")
async def list_members(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    result = await db.execute(
        select(SyncshellMember.member_user_id).where(
            SyncshellMember.user_id == ctx.user.id
        )
    )
    member_ids = result.scalars().all()
    if not member_ids:
        return {"members": []}

    user_result = await db.execute(select(User).where(User.id.in_(member_ids)))
    users = {user.id: user for user in user_result.scalars().all()}

    presence_result = await db.execute(
        select(SyncshellPresence).where(
            SyncshellPresence.user_id == ctx.user.id,
            SyncshellPresence.member_user_id.in_(member_ids),
        )
    )
    presences = {p.member_user_id: p for p in presence_result.scalars().all()}

    members: list[dict[str, Any]] = []
    for member_id in member_ids:
        presence = presences.get(member_id)
        members.append(
            {
                "id": str(member_id),
                "displayName": _display_name(users.get(member_id)),
                "active": bool(presence.active) if presence else False,
                "lastSeen": _to_iso(presence.last_seen) if presence else None,
            }
        )
    members.sort(key=lambda entry: entry["displayName"].lower())
    return {"members": members}


@router.post("/presence")
async def update_presence(
    payload: PresenceUpdateRequest,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    result = await db.execute(
        select(SyncshellMember.member_user_id).where(
            SyncshellMember.user_id == ctx.user.id
        )
    )
    valid_members = set(result.scalars().all())
    active_members = {mid for mid in payload.active_member_ids if mid in valid_members}

    presence_result = await db.execute(
        select(SyncshellPresence).where(SyncshellPresence.user_id == ctx.user.id)
    )
    existing = {record.member_user_id: record for record in presence_result.scalars().all()}
    now = datetime.utcnow()

    for member_id in valid_members:
        record = existing.get(member_id)
        if member_id in active_members:
            if record:
                record.active = True
                record.last_seen = now
            else:
                db.add(
                    SyncshellPresence(
                        user_id=ctx.user.id,
                        member_user_id=member_id,
                        active=True,
                        last_seen=now,
                    )
                )
        else:
            if record and record.active:
                record.active = False
    await db.commit()
    return {"status": "ok"}


@router.get("/presence")
async def get_presence(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    result = await db.execute(
        select(SyncshellMember.member_user_id).where(
            SyncshellMember.user_id == ctx.user.id
        )
    )
    member_ids = result.scalars().all()
    if not member_ids:
        return {"presence": [], "currentlySynced": []}

    presence_result = await db.execute(
        select(SyncshellPresence).where(
            SyncshellPresence.user_id == ctx.user.id,
            SyncshellPresence.member_user_id.in_(member_ids),
        )
    )
    presence_map = {
        record.member_user_id: record for record in presence_result.scalars().all()
    }

    user_result = await db.execute(select(User).where(User.id.in_(member_ids)))
    users = {user.id: user for user in user_result.scalars().all()}

    presence_entries: list[dict[str, Any]] = []
    active_entries: list[dict[str, Any]] = []
    for member_id in member_ids:
        record = presence_map.get(member_id)
        entry = {
            "id": str(member_id),
            "displayName": _display_name(users.get(member_id)),
            "active": bool(record.active) if record else False,
            "lastSeen": _to_iso(record.last_seen) if record else None,
        }
        presence_entries.append(entry)
        if entry["active"]:
            active_entries.append(entry)

    presence_entries.sort(key=lambda entry: entry["displayName"].lower())
    active_entries.sort(key=lambda entry: entry["displayName"].lower())
    return {"presence": presence_entries, "currentlySynced": active_entries}


@router.post("/pair")
async def pair(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    """Issue a short-lived pairing token for a client."""
    await _check_rate_limit(ctx.user.id, db)
    token = uuid4().hex
    pairing = await db.get(SyncshellPairing, ctx.user.id)
    if pairing:
        pairing.token = token
        pairing.created_at = datetime.utcnow()
        pairing.expires_at = datetime.utcnow() + timedelta(seconds=TOKEN_TTL)
    else:
        pairing = SyncshellPairing(
            user_id=ctx.user.id,
            token=token,
            created_at=datetime.utcnow(),
            expires_at=datetime.utcnow() + timedelta(seconds=TOKEN_TTL),
        )
        db.add(pairing)
    await db.commit()
    return {"token": token}


@router.post("/manifest")
async def upload_manifest(
    manifest: list[dict[str, Any]],
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    """Receive a hashed file manifest from a client.

    The manifest is capped in size to prevent excessive memory usage and
    naive clients from overwhelming the API.
    """
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    payload_size = len(str(manifest).encode())
    if payload_size > MAX_MANIFEST_BYTES:
        raise HTTPException(status_code=413, detail="manifest too large")
    manifest_json = json.dumps(manifest)
    record = await db.get(SyncshellManifest, ctx.user.id)
    if record:
        record.manifest_json = manifest_json
        record.updated_at = datetime.utcnow()
    else:
        record = SyncshellManifest(user_id=ctx.user.id, manifest_json=manifest_json)
        db.add(record)
    await db.commit()
    return {"status": "ok"}


@router.post("/asset/upload")
async def request_asset_upload(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, str]:
    """Return a pre-signed URL for chunked asset upload."""
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    try:
        url = await presign_upload()
    except Exception as e:  # pragma: no cover - network failure
        raise HTTPException(status_code=502, detail="vault unavailable") from e
    return {"url": url}


@router.get("/asset/download/{asset_id}")
async def request_asset_download(
    asset_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, str]:
    """Return a pre-signed URL for asset download."""
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    try:
        url = await presign_download(asset_id)
    except Exception as e:  # pragma: no cover - network failure
        raise HTTPException(status_code=502, detail="vault unavailable") from e
    return {"url": url}


async def _clear_manifest(ctx: RequestContext, db: AsyncSession) -> None:
    record = await db.get(SyncshellManifest, ctx.user.id)
    if record:
        await db.delete(record)
        await db.commit()


@router.post("/resync")
async def resync(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, str]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    await _clear_manifest(ctx, db)
    return {"status": "ok"}


@router.post("/cache")
async def clear_cache(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, str]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    await _clear_manifest(ctx, db)
    return {"status": "ok"}
