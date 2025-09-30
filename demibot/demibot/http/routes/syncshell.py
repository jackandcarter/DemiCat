from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import os
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any, Optional
from uuid import uuid4

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field
from sqlalchemy import func, or_, select
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


logger = logging.getLogger(__name__)


router = APIRouter(prefix="/api/syncshell", tags=["syncshell"])

RATE_LIMIT = int(os.getenv("SYNC_SHELL_MAX_REQUESTS_PER_MINUTE", "30"))
TOKEN_TTL = int(os.getenv("SYNC_SHELL_TOKEN_TTL", "300"))  # seconds
MAX_MANIFEST_BYTES = 1024 * 1024  # 1 MiB manifest payload cap

DEFAULT_TRANSFER_LIMITS: dict[str, int] = {
    "bytesPerSecond": int(os.getenv("SYNC_SHELL_BYTES_PER_SECOND", str(512 * 1024))),
    "chunkSizeBytes": int(os.getenv("SYNC_SHELL_CHUNK_SIZE", str(64 * 1024))),
    "maxOutstandingWants": int(os.getenv("SYNC_SHELL_MAX_OUTSTANDING_WANTS", "64")),
}

TRANSFER_BUDGET_BYTES = int(
    os.getenv("SYNC_SHELL_TRANSFER_BUDGET_BYTES", str(512 * 1024 * 1024))
)
TRANSFER_BUDGET_WINDOW_SECONDS = int(
    os.getenv("SYNC_SHELL_TRANSFER_BUDGET_WINDOW", str(60 * 60))
)


@dataclass
class _TransferBudget:
    window_start: datetime
    used_bytes: int = 0


_transfer_budgets: dict[int, _TransferBudget] = {}

_peer_discovery_lock: asyncio.Lock = asyncio.Lock()
_peer_discovery_state: dict[tuple[int, int], dict[str, dict[str, Any]]] = {}


def _clone_discovery_asset(asset: dict[str, Any]) -> dict[str, Any]:
    clone = dict(asset)
    items = clone.get("items")
    if isinstance(items, list):
        clone["items"] = [
            _clone_discovery_asset(item)
            for item in items
            if isinstance(item, dict)
        ]
    deps = clone.get("dependencies")
    if isinstance(deps, list):
        clone["dependencies"] = [
            str(dep)
            for dep in deps
            if isinstance(dep, (str, int, float))
        ]
    return clone


def _snapshot_discovery_assets(
    assets: dict[str, dict[str, Any]]
) -> dict[str, dict[str, Any]]:
    return {key: _clone_discovery_asset(value) for key, value in assets.items()}


def _normalise_dependencies(value: Any) -> list[str]:
    if not isinstance(value, list):
        return []
    dependencies: list[str] = []
    for entry in value:
        if isinstance(entry, str) and entry.strip():
            dependencies.append(entry.strip())
    return dependencies


def _derive_mod_asset_id(
    collection_id: str, mod: dict[str, Any]
) -> str:
    mod_id = str(mod.get("modId") or "").strip()
    mod_hash = str(mod.get("hash") or "").strip()
    components: list[str] = [collection_id] if collection_id else []
    if mod_id:
        components.append(mod_id)
    elif mod_hash:
        components.append(mod_hash)
    if components:
        return "::".join(components)
    try:
        serialised = json.dumps(mod, sort_keys=True)
    except (TypeError, ValueError):
        serialised = repr(mod)
    digest = hashlib.sha256(serialised.encode()).hexdigest()
    return digest


def _derive_patch_asset_id(
    collection_id: str, mod_id: str | None, patch: dict[str, Any]
) -> str:
    patch_path = str(patch.get("path") or "").strip()
    patch_hash = str(patch.get("hash") or "").strip()
    components: list[str] = []
    if collection_id:
        components.append(collection_id)
    if mod_id:
        components.append(str(mod_id))
    if patch_path:
        components.append(patch_path)
    elif patch_hash:
        components.append(patch_hash)
    if components:
        return "::".join(components)
    try:
        serialised = json.dumps(patch, sort_keys=True)
    except (TypeError, ValueError):
        serialised = repr(patch)
    digest = hashlib.sha256(serialised.encode()).hexdigest()
    return digest


def build_discovery_assets(
    manifest: dict[str, Any], uploader: User | None
) -> dict[str, dict[str, Any]]:
    assets: dict[str, dict[str, Any]] = {}
    uploader_label = _display_name(uploader)

    collections = manifest.get("collections") or []
    for collection in collections:
        if not isinstance(collection, dict):
            continue
        collection_id = str(collection.get("collectionId") or "default").strip()

        mods = collection.get("mods") or []
        for mod in mods:
            if not isinstance(mod, dict):
                continue
            asset_id = _derive_mod_asset_id(collection_id, mod)
            if not asset_id:
                continue
            entry: dict[str, Any] = {
                "id": asset_id,
                "name": (mod.get("name") or mod.get("modId") or asset_id) or asset_id,
                "kind": str(mod.get("kind") or "MOD").upper(),
                "uploader": uploader_label,
            }
            size = _normalise_size(mod.get("size"))
            if size > 0:
                entry["size"] = size
            created_at = mod.get("createdAt") or mod.get("created_at")
            if isinstance(created_at, str) and created_at.strip():
                entry["createdAt"] = created_at
            updated_at = mod.get("updatedAt") or mod.get("updated_at")
            if isinstance(updated_at, str) and updated_at.strip():
                entry["updatedAt"] = updated_at
            dependencies = _normalise_dependencies(
                mod.get("dependencies") or mod.get("deps")
            )
            if dependencies:
                entry["dependencies"] = dependencies

            child_items: list[dict[str, Any]] = []
            files = mod.get("files") or []
            for file_entry in files:
                if not isinstance(file_entry, dict):
                    continue
                child_id_source = str(
                    file_entry.get("hash") or file_entry.get("path") or ""
                ).strip()
                if not child_id_source:
                    continue
                child_id = f"{asset_id}::{child_id_source}"
                child_payload: dict[str, Any] = {
                    "id": child_id,
                    "name": file_entry.get("path") or child_id_source,
                    "kind": "FILE",
                    "uploader": uploader_label,
                }
                child_size = _normalise_size(file_entry.get("size"))
                if child_size > 0:
                    child_payload["size"] = child_size
                child_items.append(child_payload)
            patches = mod.get("patches") or []
            for patch in patches:
                if not isinstance(patch, dict):
                    continue
                patch_id = _derive_patch_asset_id(collection_id, mod.get("modId"), patch)
                patch_payload: dict[str, Any] = {
                    "id": patch_id,
                    "name": patch.get("path") or patch_id,
                    "kind": "PATCH",
                    "uploader": uploader_label,
                }
                patch_size = _normalise_size(patch.get("size"))
                if patch_size > 0:
                    patch_payload["size"] = patch_size
                child_items.append(patch_payload)
            if child_items:
                entry["items"] = child_items
            assets[asset_id] = entry

        collection_patches = collection.get("patches") or []
        for patch in collection_patches:
            if not isinstance(patch, dict):
                continue
            patch_id = _derive_patch_asset_id(collection_id, None, patch)
            if not patch_id:
                continue
            entry = {
                "id": patch_id,
                "name": patch.get("path") or patch_id,
                "kind": "PATCH",
                "uploader": uploader_label,
            }
            patch_size = _normalise_size(patch.get("size"))
            if patch_size > 0:
                entry["size"] = patch_size
            assets[patch_id] = entry

    return assets


async def compute_discovery_delta(
    recipient_id: int,
    uploader_id: int,
    assets: dict[str, dict[str, Any]],
) -> tuple[list[dict[str, Any]], list[str]]:
    async with _peer_discovery_lock:
        previous = _peer_discovery_state.get((recipient_id, uploader_id), {})
        updated: list[dict[str, Any]] = []
        for asset_id, payload in assets.items():
            previous_payload = previous.get(asset_id)
            if previous_payload != payload:
                updated.append(_clone_discovery_asset(payload))
        removed = [asset_id for asset_id in previous.keys() if asset_id not in assets]
        _peer_discovery_state[(recipient_id, uploader_id)] = _snapshot_discovery_assets(
            assets
        )
    return updated, removed


async def get_memberships_for_recipient(
    member_user_id: int, db: AsyncSession
) -> list[int]:
    result = await db.execute(
        select(SyncshellMember.user_id).where(
            SyncshellMember.member_user_id == member_user_id
        )
    )
    return list(result.scalars().all())


def peer_delta_timestamp() -> str:
    return _to_iso(datetime.utcnow())


def _now_utc() -> datetime:
    return datetime.utcnow()


def _normalise_size(value: Any) -> int:
    try:
        if value is None:
            return 0
        if isinstance(value, (int, float)):
            return int(value)
        if isinstance(value, str) and value.strip():
            return int(float(value))
    except (ValueError, TypeError):
        return 0
    return 0


def _extract_manifest_assets(manifest: Any) -> dict[tuple[Any, ...], dict[str, Any]]:
    assets: dict[tuple[Any, ...], dict[str, Any]] = {}
    if not isinstance(manifest, dict):
        return assets

    collections = manifest.get("collections") or []
    for collection in collections:
        if not isinstance(collection, dict):
            continue
        collection_id = collection.get("collectionId") or "default"

        mods = collection.get("mods") or []
        for mod in mods:
            if not isinstance(mod, dict):
                continue
            mod_id = mod.get("modId") or ""
            mod_key = ("mod", collection_id, mod_id)
            assets[mod_key] = {
                "kind": "mod",
                "collectionId": collection_id,
                "modId": mod_id,
                "hash": mod.get("hash"),
                "size": _normalise_size(mod.get("size")),
                "transferable": False,
            }

            files = mod.get("files") or []
            for file_entry in files:
                if not isinstance(file_entry, dict):
                    continue
                path = file_entry.get("path") or ""
                file_key = ("file", collection_id, mod_id, path)
                assets[file_key] = {
                    "kind": "file",
                    "collectionId": collection_id,
                    "modId": mod_id,
                    "path": path,
                    "hash": file_entry.get("hash"),
                    "size": _normalise_size(file_entry.get("size")),
                    "transferable": True,
                }

            patches = mod.get("patches") or []
            for patch in patches:
                if not isinstance(patch, dict):
                    continue
                path = patch.get("path") or ""
                patch_key = ("mod_patch", collection_id, mod_id, path)
                assets[patch_key] = {
                    "kind": "mod_patch",
                    "collectionId": collection_id,
                    "modId": mod_id,
                    "path": path,
                    "hash": patch.get("hash"),
                    "size": _normalise_size(patch.get("size")),
                    "transferable": True,
                }

        collection_patches = collection.get("patches") or []
        for patch in collection_patches:
            if not isinstance(patch, dict):
                continue
            path = patch.get("path") or ""
            patch_key = ("patch", collection_id, path)
            assets[patch_key] = {
                "kind": "patch",
                "collectionId": collection_id,
                "path": path,
                "hash": patch.get("hash"),
                "size": _normalise_size(patch.get("size")),
                "transferable": True,
            }

    return assets


def _serialise_asset(asset: dict[str, Any]) -> dict[str, Any]:
    payload: dict[str, Any] = {"kind": asset.get("kind"), "hash": asset.get("hash")}
    if asset.get("collectionId"):
        payload["collectionId"] = asset.get("collectionId")
    if asset.get("modId"):
        payload["modId"] = asset.get("modId")
    if asset.get("path"):
        payload["path"] = asset.get("path")
    size = asset.get("size")
    if isinstance(size, int) and size > 0:
        payload["size"] = size
    return payload


def _serialise_conflict(
    current: dict[str, Any], previous: dict[str, Any]
) -> dict[str, Any]:
    payload = {
        "kind": current.get("kind") or previous.get("kind"),
        "hash": current.get("hash"),
        "expected": previous.get("hash"),
    }
    if current.get("collectionId") or previous.get("collectionId"):
        payload["collectionId"] = current.get("collectionId") or previous.get(
            "collectionId"
        )
    if current.get("modId") or previous.get("modId"):
        payload["modId"] = current.get("modId") or previous.get("modId")
    if current.get("path") or previous.get("path"):
        payload["path"] = current.get("path") or previous.get("path")

    current_size = current.get("size")
    if isinstance(current_size, int) and current_size >= 0:
        payload["size"] = current_size
    previous_size = previous.get("size")
    if isinstance(previous_size, int) and previous_size >= 0:
        payload["previousSize"] = previous_size
    return payload


def _compute_manifest_diff(
    previous: Optional[dict[str, Any]], current: dict[str, Any]
) -> dict[str, list[dict[str, Any]]]:
    previous_assets = _extract_manifest_assets(previous or {})
    current_assets = _extract_manifest_assets(current)

    need: dict[tuple[Any, ...], dict[str, Any]] = {}
    remove: dict[tuple[Any, ...], dict[str, Any]] = {}
    conflicts: list[dict[str, Any]] = []

    previous_keys = set(previous_assets)
    current_keys = set(current_assets)

    for key in current_keys - previous_keys:
        asset = current_assets[key]
        if asset.get("transferable") and asset.get("hash"):
            need[key] = _serialise_asset(asset)

    for key in previous_keys - current_keys:
        asset = previous_assets[key]
        if asset.get("transferable") and asset.get("hash"):
            remove[key] = _serialise_asset(asset)

    for key in current_keys & previous_keys:
        current_asset = current_assets[key]
        previous_asset = previous_assets[key]
        if (current_asset.get("hash") or "") != (previous_asset.get("hash") or ""):
            conflicts.append(_serialise_conflict(current_asset, previous_asset))
            if current_asset.get("transferable") and current_asset.get("hash"):
                need[key] = _serialise_asset(current_asset)
            if previous_asset.get("transferable") and previous_asset.get("hash"):
                remove[key] = _serialise_asset(previous_asset)

    need_list = sorted(need.values(), key=lambda item: item.get("hash", ""))
    remove_list = sorted(remove.values(), key=lambda item: item.get("hash", ""))
    conflicts.sort(
        key=lambda item: (
            item.get("collectionId", ""),
            item.get("modId", ""),
            item.get("path", ""),
            item.get("kind", ""),
        )
    )

    return {"need": need_list, "remove": remove_list, "conflicts": conflicts}


def _update_transfer_budget(
    user_id: int, diff: dict[str, list[dict[str, Any]]]
) -> dict[str, Any]:
    limit = max(0, TRANSFER_BUDGET_BYTES)
    window = timedelta(seconds=max(1, TRANSFER_BUDGET_WINDOW_SECONDS))
    now = _now_utc()

    budget = _transfer_budgets.get(user_id)
    if not budget or now - budget.window_start >= window:
        budget = _TransferBudget(window_start=now, used_bytes=0)
        _transfer_budgets[user_id] = budget

    incremental = 0
    for entry in diff.get("need", []):
        size = entry.get("size")
        if isinstance(size, int) and size > 0:
            incremental += size

    budget.used_bytes += incremental
    if budget.used_bytes < 0:
        budget.used_bytes = 0

    remaining = max(0, limit - budget.used_bytes)
    window_end = budget.window_start + window
    return {
        "limitBytes": limit,
        "usedBytes": budget.used_bytes,
        "throttleAfterBytes": remaining,
        "windowEndsAt": _to_iso(window_end),
    }


async def handle_manifest_upload(
    manifest: dict[str, Any], ctx: RequestContext, db: AsyncSession
) -> tuple[dict[str, list[dict[str, Any]]], dict[str, Any]]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)

    manifest_json = json.dumps(manifest)
    payload_size = len(manifest_json.encode())
    if payload_size > MAX_MANIFEST_BYTES:
        raise HTTPException(status_code=413, detail="manifest too large")

    record = await db.get(SyncshellManifest, ctx.user.id)
    previous_manifest: Optional[dict[str, Any]] = None
    if record:
        try:
            previous_manifest = json.loads(record.manifest_json)
        except json.JSONDecodeError as exc:
            logger.warning(
                "syncshell.manifest.previous.decode_failed user_id=%s",
                ctx.user.id,
                exc_info=exc,
            )
            previous_manifest = None

    diff = _compute_manifest_diff(previous_manifest, manifest)

    if record:
        record.manifest_json = manifest_json
        record.updated_at = datetime.utcnow()
    else:
        record = SyncshellManifest(user_id=ctx.user.id, manifest_json=manifest_json)
        db.add(record)

    await db.commit()

    limits_payload = {
        "budget": _update_transfer_budget(ctx.user.id, diff),
        "transfer": dict(DEFAULT_TRANSFER_LIMITS),
    }
    return diff, limits_payload


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


def _normalize_display_name(value: str | None) -> str:
    return (value or "").strip().casefold()


def _to_iso(dt: datetime | None) -> str | None:
    if not dt:
        return None
    if dt.tzinfo is not None:
        dt = dt.astimezone(timezone.utc)
    dt = dt.replace(tzinfo=None, microsecond=0)
    return dt.isoformat() + "Z"


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


def _presence_payload(
    presence: SyncshellPresence | None,
) -> tuple[str, str | None, str | None, str | None]:
    if not presence:
        return "offline", None, None, None

    presence_value = "online" if presence.active else "offline"
    last_seen = _to_iso(presence.last_seen)
    sync_status = "syncing" if presence.active else None
    synced_at = last_seen if presence.active else None
    return presence_value, sync_status, last_seen, synced_at


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
    invites = [
        _serialize_invite(invite, "outgoing") for invite in result.scalars().all()
    ]
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
    original_member = (payload.member or "").strip()
    normalized_member = _normalize_display_name(original_member)
    display_name = original_member
    if payload.member_id is not None:
        target_user = await db.get(User, payload.member_id)
        if not target_user:
            raise HTTPException(status_code=404, detail="member not found")
        display_name = _display_name(target_user)
    elif normalized_member:
        potential_targets = await db.execute(
            select(User).where(
                or_(
                    func.lower(User.global_name) == normalized_member,
                    func.lower(User.character_name) == normalized_member,
                )
            )
        )
        matches = potential_targets.scalars().all()
        if len(matches) == 1:
            target_user = matches[0]
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
        normalized_display_name = normalized_member
        existing_invite = await db.execute(
            select(SyncshellInvite).where(
                SyncshellInvite.inviter_id == ctx.user.id,
                SyncshellInvite.target_user_id.is_(None),
                func.lower(SyncshellInvite.target_display_name)
                == normalized_display_name,
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
    db: AsyncSession, invite_id: str, target_user: User
) -> SyncshellInvite:
    invite = await db.get(SyncshellInvite, invite_id)
    if not invite:
        raise HTTPException(status_code=404, detail="invite not found")
    if invite.status != "pending":
        raise HTTPException(status_code=400, detail="invite already processed")
    if invite.target_user_id is not None:
        if invite.target_user_id != target_user.id:
            raise HTTPException(status_code=404, detail="invite not found")
        return invite

    normalized_target = _normalize_display_name(_display_name(target_user))
    if not normalized_target:
        raise HTTPException(status_code=404, detail="invite not found")
    normalized_invite_target = _normalize_display_name(invite.target_display_name)
    if normalized_invite_target != normalized_target:
        raise HTTPException(status_code=404, detail="invite not found")
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
    invite = await _get_invite_for_target(db, invite_id, ctx.user)
    if invite.target_user_id is None:
        invite.target_user_id = ctx.user.id
        invite.target_display_name = _display_name(ctx.user)
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
    invite = await _get_invite_for_target(db, invite_id, ctx.user)
    if invite.target_user_id is None:
        invite.target_user_id = ctx.user.id
        invite.target_display_name = _display_name(ctx.user)
    await _finalize_invite(db, invite, accepted=False)
    return {"status": "denied"}


@router.post("/requests/{invite_id}/approve")
async def approve_request(
    invite_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    await accept_invite(invite_id, ctx=ctx, db=db)
    return {"status": "accepted"}


@router.post("/requests/{invite_id}/deny")
async def deny_request(
    invite_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    await deny_invite(invite_id, ctx=ctx, db=db)
    return {"status": "denied"}


@router.get("/pending")
async def list_pending(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)
    normalized_target = _normalize_display_name(_display_name(ctx.user))
    result = await db.execute(
        select(SyncshellInvite)
        .where(
            SyncshellInvite.status == "pending",
            or_(
                SyncshellInvite.target_user_id == ctx.user.id,
                SyncshellInvite.target_user_id.is_(None),
            ),
        )
        .order_by(SyncshellInvite.created_at.desc())
    )
    invites = [
        invite
        for invite in result.scalars().all()
        if invite.target_user_id == ctx.user.id
        or (
            normalized_target
            and _normalize_display_name(invite.target_display_name)
            == normalized_target
        )
    ]
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
                "tokenLinked": presence is not None,
            }
        )
    members.sort(key=lambda entry: entry["displayName"].lower())
    return {"members": members}


@router.get("/memberships")
async def list_memberships(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    await _require_pairing(ctx, db)
    await _check_rate_limit(ctx.user.id, db)

    member_result = await db.execute(
        select(SyncshellMember.member_user_id).where(
            SyncshellMember.user_id == ctx.user.id
        )
    )
    member_ids = list(member_result.scalars().all())

    presence_map: dict[int, SyncshellPresence] = {}
    if member_ids:
        presence_result = await db.execute(
            select(SyncshellPresence).where(
                SyncshellPresence.user_id == ctx.user.id,
                SyncshellPresence.member_user_id.in_(member_ids),
            )
        )
        presence_map = {
            record.member_user_id: record for record in presence_result.scalars().all()
        }

    users: dict[int, User] = {}
    if member_ids:
        user_result = await db.execute(select(User).where(User.id.in_(member_ids)))
        users = {user.id: user for user in user_result.scalars().all()}

    members: list[dict[str, Any]] = []
    currently_synced: list[dict[str, Any]] = []
    for member_id in member_ids:
        user = users.get(member_id)
        presence = presence_map.get(member_id)
        presence_value, sync_status, last_seen, synced_at = _presence_payload(presence)
        entry: dict[str, Any] = {
            "id": str(member_id),
            "displayName": _display_name(user),
            "presence": presence_value,
            "syncStatus": sync_status,
            "lastSeen": last_seen,
            "tokenLinked": presence is not None,
        }
        if synced_at:
            entry["syncedAt"] = synced_at
        members.append(entry)

        if presence and presence.active:
            active_entry = {
                "id": entry["id"],
                "displayName": entry["displayName"],
                "presence": presence_value,
                "syncStatus": sync_status,
                "lastSeen": last_seen,
                "tokenLinked": True,
            }
            if synced_at:
                active_entry["syncedAt"] = synced_at
            currently_synced.append(active_entry)

    members.sort(key=lambda entry: entry["displayName"].lower())
    currently_synced.sort(key=lambda entry: entry["displayName"].lower())

    invites_result = await db.execute(
        select(SyncshellInvite)
        .where(SyncshellInvite.inviter_id == ctx.user.id)
        .order_by(SyncshellInvite.updated_at.desc())
    )
    invites = [
        _serialize_invite(invite, "outgoing") for invite in invites_result.scalars().all()
    ]

    normalized_pending = _normalize_display_name(_display_name(ctx.user))
    pending_result = await db.execute(
        select(SyncshellInvite)
        .where(
            SyncshellInvite.status == "pending",
            or_(
                SyncshellInvite.target_user_id == ctx.user.id,
                SyncshellInvite.target_user_id.is_(None),
            ),
        )
        .order_by(SyncshellInvite.created_at.desc())
    )
    pending_invites = [
        invite
        for invite in pending_result.scalars().all()
        if invite.target_user_id == ctx.user.id
        or (
            normalized_pending
            and _normalize_display_name(invite.target_display_name)
            == normalized_pending
        )
    ]
    inviter_ids = {invite.inviter_id for invite in pending_invites}
    inviters: dict[int, User] = {}
    if inviter_ids:
        inviter_result = await db.execute(select(User).where(User.id.in_(inviter_ids)))
        inviters = {user.id: user for user in inviter_result.scalars().all()}

    pending_approvals: list[dict[str, Any]] = []
    for invite in pending_invites:
        inviter = inviters.get(invite.inviter_id)
        pending_approvals.append(
            {
                "id": invite.id,
                "requesterId": invite.inviter_id,
                "displayName": _display_name(inviter),
                "requestedAt": _to_iso(invite.created_at),
                "direction": "incoming",
            }
        )

    return {
        "members": members,
        "currentlySynced": currently_synced,
        "pendingApprovals": pending_approvals,
        "invites": invites,
    }


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
    existing = {
        record.member_user_id: record for record in presence_result.scalars().all()
    }
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
    manifest: dict[str, Any],
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    """Receive a hashed file manifest from a client.

    The manifest is capped in size to prevent excessive memory usage and
    naive clients from overwhelming the API.
    """
    diff, limits = await handle_manifest_upload(manifest, ctx, db)
    return {"status": "ok", "diff": diff, "limits": limits}


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
