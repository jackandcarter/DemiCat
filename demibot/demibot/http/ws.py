from __future__ import annotations

import asyncio
from dataclasses import dataclass
from typing import Dict

import logging
from fastapi import WebSocket, WebSocketDisconnect

from ..db.session import get_session
from .deps import RequestContext, api_key_auth
from .ws_common import (
    HEARTBEAT_PAYLOAD,
    SEND_TIMEOUT,
    BaseConnectionManager,
    authenticate_websocket,
)

# Mapping of websocket paths to roles required to access them.
# Additional entries can be added as new protected paths are introduced.
PROTECTED_PATH_ROLES: dict[str, str] = {"/ws/officer-messages": "officer"}


@dataclass
class ConnectionInfo:
    guild_id: int
    roles: list[str]
    path: str


class ConnectionManager(BaseConnectionManager[ConnectionInfo]):
    heartbeat_payload = HEARTBEAT_PAYLOAD

    def __init__(self) -> None:
        super().__init__()
        self.connections: Dict[WebSocket, ConnectionInfo] = {}

    def _create_connection(
        self, websocket: WebSocket, ctx: RequestContext
    ) -> ConnectionInfo:
        return ConnectionInfo(
            ctx.guild.id, ctx.roles, websocket.scope.get("path", "")
        )

    async def broadcast_text(
        self,
        message: str,
        guild_id: int,
        officer_only: bool = False,
        path: str | None = None,
    ) -> None:
        targets: list[WebSocket] = []
        coros: list[asyncio.Future] = []
        for ws, info in list(self.connections.items()):
            if info.guild_id != guild_id:
                continue
            if path is not None:
                if info.path != path:
                    continue
            elif officer_only:
                if info.path != "/ws/officer-messages":
                    continue
            elif info.path == "/ws/officer-messages":
                continue
            if officer_only and "officer" not in info.roles:
                continue
            targets.append(ws)
            coros.append(asyncio.wait_for(ws.send_text(message), SEND_TIMEOUT))
        results = await asyncio.gather(*coros, return_exceptions=True)
        for ws, result in zip(targets, results):
            if isinstance(result, Exception):
                self.disconnect(ws)

manager = ConnectionManager()

async def websocket_endpoint(websocket: WebSocket) -> None:
    path = websocket.scope.get("path", "")
    required_role = PROTECTED_PATH_ROLES.get(path)
    ctx = await authenticate_websocket(
        websocket,
        require_role=required_role,
        auth_func=api_key_auth,
        session_factory=get_session,
    )
    if ctx is None:
        return

    logging.debug("WS %s authenticated, invoking manager.connect", path)
    await manager.connect(websocket, ctx)
    logging.info(
        "WS %s guild=%s user=%s",
        path,
        ctx.guild.discord_guild_id,
        ctx.user.discord_user_id,
    )
    try:
        while True:
            result = await websocket.receive()
            if result["type"] == "websocket.disconnect":
                break
    except (WebSocketDisconnect, RuntimeError):
        manager.disconnect(websocket)
        return
    manager.disconnect(websocket)
