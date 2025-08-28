
from __future__ import annotations
import asyncio
import time
from dataclasses import dataclass, field
from typing import Dict

from fastapi import HTTPException, WebSocket, WebSocketDisconnect
import logging

from ..db.session import get_session
from .deps import RequestContext, api_key_auth

PING_INTERVAL = 30.0
PING_TIMEOUT = 60.0
SEND_TIMEOUT = 5.0


@dataclass
class ConnectionInfo:
    guild_id: int
    roles: list[str]
    path: str
    last_activity: float = field(default_factory=time.monotonic)


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

    def touch(self, websocket: WebSocket) -> None:
        info = self.connections.get(websocket)
        if info is not None:
            info.last_activity = time.monotonic()

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
                now = time.monotonic()
                dead: list[WebSocket] = []
                for ws, info in list(self.connections.items()):
                    if now - info.last_activity > PING_TIMEOUT:
                        try:
                            await ws.close()
                        finally:
                            dead.append(ws)
                        continue
                    try:
                        await ws.send_text("ping")
                    except Exception:
                        dead.append(ws)
                for ws in dead:
                    self.disconnect(ws)
        except asyncio.CancelledError:
            pass

manager = ConnectionManager()

async def websocket_endpoint(websocket: WebSocket) -> None:
    token = websocket.headers.get("X-Api-Key") or websocket.query_params.get("token")
    if not token:
        await websocket.close(code=1008)
        return
    async for db in get_session():
        try:
            ctx = await api_key_auth(x_api_key=token, db=db)
        except HTTPException:
            await websocket.close(code=1008)
            return
        break
    await manager.connect(websocket, ctx)
    logging.info(
        "WS %s guild=%s user=%s",
        websocket.scope.get("path", ""),
        ctx.guild.discord_guild_id,
        ctx.user.discord_user_id,
    )
    try:
        while True:
            await websocket.receive_text()
            manager.touch(websocket)
    except WebSocketDisconnect:
        manager.disconnect(websocket)
