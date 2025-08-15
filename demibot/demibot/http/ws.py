
from __future__ import annotations
from typing import Dict, List, Tuple

from fastapi import HTTPException, WebSocket, WebSocketDisconnect

from ..db.session import get_session
from .deps import RequestContext, api_key_auth

class ConnectionManager:
    def __init__(self) -> None:
        self.connections: Dict[WebSocket, Tuple[int, List[str]]] = {}

    async def connect(self, websocket: WebSocket, ctx: RequestContext) -> None:
        await websocket.accept()
        self.connections[websocket] = (ctx.guild.id, ctx.roles)

    def disconnect(self, websocket: WebSocket) -> None:
        if websocket in self.connections:
            del self.connections[websocket]

    async def broadcast_text(
        self, message: str, guild_id: int, officer_only: bool = False
    ) -> None:
        dead: list[WebSocket] = []
        for ws, (gid, roles) in list(self.connections.items()):
            if gid != guild_id:
                continue
            if officer_only and "officer" not in roles:
                continue
            try:
                await ws.send_text(message)
            except Exception:
                dead.append(ws)
        for ws in dead:
            self.disconnect(ws)

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
    try:
        while True:
            await websocket.receive_text()  # keep alive; plugin ignores inbound anyway
    except WebSocketDisconnect:
        manager.disconnect(websocket)
