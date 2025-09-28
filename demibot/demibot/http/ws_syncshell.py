from __future__ import annotations

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


def _ws_request(ws: WebSocket) -> SimpleNamespace:
    client_host = getattr(getattr(ws, "client", None), "host", "unknown")
    return SimpleNamespace(
        client=SimpleNamespace(host=client_host),
        method="WS",
        url=SimpleNamespace(path=ws.scope.get("path", "")),
    )


async def _handle_manifest_message(
    websocket: WebSocket, ctx: RequestContext, payload: dict[str, Any]
) -> bool:
    manifest_payload = payload.get("manifest")
    if not isinstance(manifest_payload, dict):
        LOGGER.warning("syncshell manifest payload missing or invalid")
        return True

    try:
        async with get_session() as db:
            diff, limits = await syncshell_routes.handle_manifest_upload(
                manifest_payload, ctx, db
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

    want_message = {
        "type": "want",
        "payload": {
            "peerId": peer_id,
            "want": {
                "blobs": [entry["hash"] for entry in diff.get("need", []) if entry.get("hash")],
                "chunks": [],
                "sizeHints": [],
            },
            "diff": diff,
            "limits": limits,
        },
    }
    await websocket.send_json(want_message)
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
        # Allow the client context manager to handle closure; best effort to
        # ensure the coroutine completes cleanly without raising.
        try:
            await websocket.close()
        except Exception:  # pragma: no cover - best effort close
            pass
