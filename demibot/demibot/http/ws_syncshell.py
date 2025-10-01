from __future__ import annotations

import asyncio
import logging
import os
from types import SimpleNamespace
from typing import Any

from fastapi import HTTPException, WebSocket, WebSocketDisconnect

from ..db.session import get_session
from .deps import RequestContext, api_key_auth
from .routes import syncshell as syncshell_routes


LOGGER = logging.getLogger(__name__)

DEFAULT_LIMITS = syncshell_routes.DEFAULT_TRANSFER_LIMITS

HELLO_MESSAGE = {"type": "hello", "payload": {"version": 1, "limits": DEFAULT_LIMITS}}

_CONNECTIONS: dict[int, set[WebSocket]] = {}
_CONNECTION_LOCK = asyncio.Lock()


def _ws_request(ws: WebSocket) -> SimpleNamespace:
    client_host = getattr(getattr(ws, "client", None), "host", "unknown")
    return SimpleNamespace(
        client=SimpleNamespace(host=client_host),
        method="WS",
        url=SimpleNamespace(path=ws.scope.get("path", "")),
    )


async def _register_connection(user_id: int, websocket: WebSocket) -> None:
    async with _CONNECTION_LOCK:
        sockets = _CONNECTIONS.setdefault(user_id, set())
        sockets.add(websocket)


async def _unregister_connection(user_id: int, websocket: WebSocket) -> None:
    async with _CONNECTION_LOCK:
        sockets = _CONNECTIONS.get(user_id)
        if not sockets:
            return
        sockets.discard(websocket)
        if not sockets:
            _CONNECTIONS.pop(user_id, None)


async def _get_connections(user_id: int) -> list[WebSocket]:
    async with _CONNECTION_LOCK:
        sockets = list(_CONNECTIONS.get(user_id, ()))
    return sockets


async def _broadcast_peer_delta(
    uploader_id: int,
    peer_identifier: str,
    assets: dict[str, dict[str, Any]],
    member_grants: list[syncshell_routes._MemberGrant],
) -> None:
    if not member_grants:
        return
    for grant in member_grants:
        connections = await _get_connections(grant.member_id)
        if not connections:
            continue
        filtered_assets = syncshell_routes._filter_discovery_assets_for_scope(
            assets, grant.scope
        )
        updated, removed = await syncshell_routes.compute_discovery_delta(
            grant.member_id, uploader_id, filtered_assets
        )
        if not updated and not removed:
            continue
        payload = {
            "type": "peerDelta",
            "payload": {
                "peerId": peer_identifier,
                "timestamp": syncshell_routes.peer_delta_timestamp(),
                "updated": updated,
                "removed": removed,
            },
        }
        dead: list[WebSocket] = []
        for peer_socket in connections:
            try:
                await peer_socket.send_json(payload)
            except Exception:
                LOGGER.warning(
                    "syncshell failed delivering peer delta to user %s",
                    grant.member_id,
                )
                dead.append(peer_socket)
        for peer_socket in dead:
            await _unregister_connection(grant.member_id, peer_socket)


async def _handle_manifest_message(
    websocket: WebSocket, ctx: RequestContext, payload: dict[str, Any]
) -> bool:
    manifest_payload = payload.get("manifest")
    if not isinstance(manifest_payload, dict):
        LOGGER.warning("syncshell manifest payload missing or invalid")
        return True

    discovery_assets: dict[str, dict[str, Any]] = {}
    member_grants: list[syncshell_routes._MemberGrant] = []
    try:
        async with get_session() as db:
            diff, limits = await syncshell_routes.handle_manifest_upload(
                manifest_payload, ctx, db
            )
            discovery_assets = syncshell_routes.build_discovery_assets(
                manifest_payload, ctx.user
            )
            member_grants = await syncshell_routes.get_memberships_for_recipient(
                ctx.user.id, db
            )
    except HTTPException as exc:
        LOGGER.warning("syncshell manifest rejected: %s", exc.detail)
        await websocket.close(code=1008, reason=exc.detail)
        return False
    except Exception:  # pragma: no cover - defensive logging
        LOGGER.exception("syncshell manifest handling failed")
        await websocket.close(code=1011, reason="internal error")
        return False

    peer_id = payload.get("peerId")
    if not peer_id:
        peer_id = str(ctx.user.discord_user_id or ctx.user.id)

    need_entries = diff.get("need", [])
    blobs: list[str] = []
    size_hints: list[dict[str, Any]] = []
    for entry in need_entries:
        blob_hash = entry.get("hash")
        if not blob_hash:
            continue
        blobs.append(blob_hash)
        size = entry.get("size")
        if isinstance(size, int) and size > 0:
            size_hints.append({"hash": blob_hash, "size": size})

    want_message = {
        "type": "want",
        "payload": {
            "peerId": peer_id,
            "want": {
                "blobs": blobs,
                "chunks": [],
                "sizeHints": size_hints,
            },
            "diff": diff,
            "limits": limits,
        },
    }
    await websocket.send_json(want_message)
    await _broadcast_peer_delta(
        ctx.user.id, peer_id, discovery_assets, member_grants
    )
    return True


def _extract_payload(message: dict[str, Any]) -> dict[str, Any]:
    payload = message.get("payload")
    if isinstance(payload, dict):
        return payload
    return message


async def websocket_endpoint_syncshell(websocket: WebSocket) -> None:
    path = websocket.scope.get("path", "")
    token = websocket.headers.get("X-Api-Key")
    if websocket.query_params.get("token"):
        LOGGER.warning("WS %s closing with 1008: token in url", path)
        await websocket.close(code=1008, reason="token in url")
        return
    if not token:
        LOGGER.warning("WS %s closing with 1008: missing token", path)
        await websocket.close(code=1008, reason="missing token")
        return

    ctx: RequestContext | None = None
    try:
        async with get_session() as db:
            ctx = await api_key_auth(
                _ws_request(websocket),
                x_api_key=token,
                x_discord_id=None,
                db=db,
            )
    except HTTPException as exc:
        LOGGER.warning("WS %s auth failed (%s): %s", path, exc.status_code, exc.detail)
        await websocket.close(code=1008, reason="auth failed")
        return
    except Exception:  # pragma: no cover - defensive logging
        LOGGER.exception("WS %s unexpected auth failure", path)
        await websocket.close(code=1011, reason="internal error")
        return

    await websocket.accept()
    if ctx is not None:
        await _register_connection(ctx.user.id, websocket)

    hello_received = False
    try:
        while True:
            try:
                message = await websocket.receive_json()
            except WebSocketDisconnect:
                break
            except RuntimeError:
                break
            except ValueError:
                LOGGER.warning("syncshell websocket received invalid JSON payload")
                await websocket.close(code=1003, reason="invalid payload")
                break

            if not isinstance(message, dict):
                continue

            message_type = message.get("type")
            payload_data = _extract_payload(message)

            if message_type == "hello":
                hello_received = True
                await websocket.send_json(HELLO_MESSAGE)
            elif message_type == "manifest":
                if not hello_received:
                    await websocket.close(code=1002, reason="handshake required")
                    break
                should_continue = await _handle_manifest_message(
                    websocket, ctx, payload_data
                )
                if not should_continue:
                    break
            else:
                LOGGER.debug("syncshell websocket ignoring message type %s", message_type)
    finally:
        if ctx is not None:
            await _unregister_connection(ctx.user.id, websocket)
        # Allow the client context manager to handle closure; best effort to
        # ensure the coroutine completes cleanly without raising.
        try:
            await websocket.close()
        except Exception:  # pragma: no cover - best effort close
            pass
