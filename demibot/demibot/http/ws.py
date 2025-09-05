
from __future__ import annotations
import asyncio
from dataclasses import dataclass
from typing import Dict

from fastapi import HTTPException, WebSocket, WebSocketDisconnect
import logging

from ..db.session import get_session
from .deps import RequestContext, api_key_auth

PING_INTERVAL = 30.0
PING_TIMEOUT = 60.0
SEND_TIMEOUT = 5.0

# Mapping of websocket paths to roles required to access them.
# Additional entries can be added as new protected paths are introduced.
PROTECTED_PATH_ROLES: dict[str, str] = {"/ws/officer-messages": "officer"}


@dataclass
class ConnectionInfo:
    guild_id: int
    roles: list[str]
    path: str


class ConnectionManager:
    def __init__(self) -> None:
        self.connections: Dict[WebSocket, ConnectionInfo] = {}
        self._ping_task: asyncio.Task | None = None

    async def connect(self, websocket: WebSocket, ctx: RequestContext) -> None:
        await websocket.accept()
        self.connections[websocket] = ConnectionInfo(
            ctx.guild.id, ctx.roles, websocket.scope.get("path", "")
        )
        if self._ping_task is None:
            try:
                loop = asyncio.get_running_loop()
            except RuntimeError:
                loop = None
            if loop is not None:
                self._ping_task = loop.create_task(self._ping_loop())

    def disconnect(self, websocket: WebSocket) -> None:
        if websocket in self.connections:
            del self.connections[websocket]
        if not self.connections and self._ping_task is not None:
            self._ping_task.cancel()
            self._ping_task = None

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
            if officer_only:
                if "officer" not in info.roles or info.path != "/ws/officer-messages":
                    continue
            elif path is not None:
                if info.path != path:
                    continue
            elif info.path == "/ws/officer-messages":
                continue
            targets.append(ws)
            coros.append(asyncio.wait_for(ws.send_text(message), SEND_TIMEOUT))
        results = await asyncio.gather(*coros, return_exceptions=True)
        for ws, result in zip(targets, results):
            if isinstance(result, Exception):
                self.disconnect(ws)

    async def _ping_loop(self) -> None:
        try:
            while True:
                await asyncio.sleep(PING_INTERVAL)
                dead: list[WebSocket] = []
                for ws in list(self.connections.keys()):
                    try:
                        await asyncio.wait_for(ws.ping(), PING_TIMEOUT)
                    except Exception:
                        dead.append(ws)
                for ws in dead:
                    self.disconnect(ws)
        except asyncio.CancelledError:
            pass

manager = ConnectionManager()

async def websocket_endpoint(websocket: WebSocket) -> None:
    path = websocket.scope.get("path", "")
    header_token = websocket.headers.get("X-Api-Key")
    query_token = websocket.query_params.get("token")
    if query_token:
        logging.warning("WS %s closing with 1008: token in url", path)
        await websocket.close(code=1008, reason="token in url")
        return
    if header_token:
        logging.debug("WS %s received X-Api-Key header", path)
    else:
        logging.debug("WS %s missing X-Api-Key header", path)
        logging.warning("WS %s closing with 1008: missing token", path)
        await websocket.close(code=1008, reason="missing token")
        return
    token = header_token

    ctx: RequestContext | None = None
    async with get_session() as db:
        try:
            ctx = await api_key_auth(
                x_api_key=token,
                x_discord_id=None,
                db=db,
            )
        except HTTPException as exc:
            logging.warning(
                "WS %s auth failed (%s): %s",
                path,
                exc.status_code,
                exc.detail,
            )
            await websocket.close(code=1008, reason="auth failed")
            return
        finally:
            await db.close()

    if ctx is None:
        logging.error("WS %s api_key_auth returned no context", path)
        await websocket.close(code=1008, reason="no context")
        return

    required_role = PROTECTED_PATH_ROLES.get(path)
    if required_role and required_role not in ctx.roles:
        logging.warning(
            "WS %s closing with 1008: missing role %s", path, required_role
        )
        await websocket.close(code=1008, reason="unauthorized")
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
            await websocket.receive()
    except WebSocketDisconnect:
        manager.disconnect(websocket)
