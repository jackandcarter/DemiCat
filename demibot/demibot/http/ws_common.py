from __future__ import annotations

import asyncio
import inspect
import logging
from types import SimpleNamespace
from typing import Dict, Generic, Optional, TypeVar

from fastapi import HTTPException, WebSocket

from importlib import import_module

from ..db.session import get_session
from .deps import RequestContext as _BaseRequestContext, api_key_auth


PING_INTERVAL = 30.0
PING_TIMEOUT = 60.0
HEARTBEAT_TIMEOUT = 5.0
HEARTBEAT_PAYLOAD = '{"op":"ping"}'
SEND_TIMEOUT = 5.0


def _ensure_request_context() -> type:
    try:
        deps_module = import_module("demibot.http.deps")
    except ModuleNotFoundError:  # pragma: no cover - defensive
        return _BaseRequestContext

    rc = getattr(deps_module, "RequestContext", _BaseRequestContext)
    try:
        parameters = inspect.signature(rc).parameters
    except (TypeError, ValueError):  # pragma: no cover - non-callable
        return rc

    if "user" in parameters and "guild" in parameters:
        return rc

    class _PatchedRequestContext(rc):  # type: ignore[misc, valid-type]
        def __init__(self, *args, **kwargs):
            user = kwargs.pop("user", None)
            guild = kwargs.pop("guild", None)
            key = kwargs.pop("key", None)
            roles = kwargs.pop("roles", None)
            if args:
                super().__init__(*args)
            else:
                super().__init__(guild, roles)
            if not hasattr(self, "user"):
                self.user = user
            if not hasattr(self, "guild"):
                self.guild = guild
            if not hasattr(self, "key"):
                self.key = key
            if not hasattr(self, "roles"):
                self.roles = roles if roles is not None else []

    deps_module.RequestContext = _PatchedRequestContext
    return _PatchedRequestContext


RequestContext = _ensure_request_context()


def build_ws_request(ws: WebSocket) -> SimpleNamespace:
    client_host = getattr(getattr(ws, "client", None), "host", "unknown")
    return SimpleNamespace(
        client=SimpleNamespace(host=client_host),
        method="WS",
        url=SimpleNamespace(path=ws.scope.get("path", "")),
    )


async def authenticate_websocket(
    websocket: WebSocket,
    *,
    logger: Optional[logging.Logger] = None,
    require_role: str | None = None,
    auth_func=api_key_auth,
    session_factory=get_session,
) -> RequestContext | None:
    log = logger or logging.getLogger(__name__)

    path = websocket.scope.get("path", "")
    header_token = websocket.headers.get("X-Api-Key")
    query_token = websocket.query_params.get("token")

    if query_token:
        log.warning("WS %s closing with 1008: token in url", path)
        await websocket.close(code=1008, reason="token in url")
        return None

    if header_token:
        log.debug("WS %s received X-Api-Key header", path)
    else:
        log.debug("WS %s missing X-Api-Key header", path)
        log.warning("WS %s closing with 1008: missing token", path)
        await websocket.close(code=1008, reason="missing token")
        return None

    ctx: RequestContext | None = None
    async with session_factory() as db:
        try:
            ctx = await auth_func(
                build_ws_request(websocket),
                x_api_key=header_token,
                x_discord_id=None,
                db=db,
            )
        except HTTPException as exc:
            log.warning(
                "WS %s auth failed (%s): %s",
                path,
                exc.status_code,
                exc.detail,
            )
            await websocket.close(code=1008, reason="auth failed")
            return None
    if ctx is None:
        log.error("WS %s api_key_auth returned no context", path)
        await websocket.close(code=1008, reason="no context")
        return None

    if require_role and require_role not in ctx.roles:
        log.warning(
            "WS %s closing with 1008: missing role %s", path, require_role
        )
        await websocket.close(code=1008, reason="unauthorized")
        return None

    return ctx


TConnection = TypeVar("TConnection")


class BaseConnectionManager(Generic[TConnection]):
    """Shared lifecycle management for websocket connection managers."""

    ping_interval: float = PING_INTERVAL
    ping_timeout: float = PING_TIMEOUT
    heartbeat_timeout: float = HEARTBEAT_TIMEOUT
    heartbeat_payload: str = HEARTBEAT_PAYLOAD

    def __init__(self) -> None:
        self.connections: Dict[WebSocket, TConnection] = {}
        self._ping_task: asyncio.Task | None = None

    async def connect(self, websocket: WebSocket, ctx: RequestContext) -> None:
        await self._accept(websocket, ctx)
        info = self._create_connection(websocket, ctx)
        self.connections[websocket] = info
        self._on_connect(websocket, info)
        if self._ping_task is None:
            try:
                loop = asyncio.get_running_loop()
            except RuntimeError:
                loop = None
            if loop is not None:
                self._ping_task = loop.create_task(self._ping_loop())

    def disconnect(self, websocket: WebSocket) -> None:
        info = self.connections.pop(websocket, None)
        if info is not None:
            self._on_disconnect(websocket, info)
        if not self.connections and self._ping_task is not None:
            self._ping_task.cancel()
            self._ping_task = None

    async def _accept(self, websocket: WebSocket, _: RequestContext) -> None:
        await websocket.accept()

    def _create_connection(
        self, websocket: WebSocket, ctx: RequestContext
    ) -> TConnection:
        del websocket  # unused default implementation
        return ctx  # type: ignore[return-value]

    def _on_connect(self, websocket: WebSocket, info: TConnection) -> None:
        del websocket, info

    def _on_disconnect(self, websocket: WebSocket, info: TConnection) -> None:
        del websocket, info

    async def _after_ping_iteration(self) -> None:
        return None

    async def _ping_loop(self) -> None:
        try:
            while True:
                await asyncio.sleep(self.ping_interval)
                dead: list[WebSocket] = []
                for ws in list(self.connections.keys()):
                    try:
                        alive = await self._probe_connection(ws)
                    except asyncio.CancelledError:
                        raise
                    except Exception:
                        alive = False
                    if not alive:
                        dead.append(ws)
                for ws in dead:
                    self.disconnect(ws)
                await self._after_ping_iteration()
        except asyncio.CancelledError:
            pass

    async def _probe_connection(self, ws: WebSocket) -> bool:
        ping = getattr(ws, "ping", None)
        supports_ping = callable(ping)
        if supports_ping:
            try:
                result = ping()
            except NotImplementedError:
                supports_ping = False
            except asyncio.CancelledError:
                raise
            except Exception:
                return False
            else:
                if result is not None and inspect.isawaitable(result):
                    try:
                        await asyncio.wait_for(result, self.ping_timeout)
                    except asyncio.CancelledError:
                        raise
                    except Exception:
                        return False
            if supports_ping:
                return True

        send_text = getattr(ws, "send_text", None)
        if callable(send_text):
            try:
                result = send_text(self.heartbeat_payload)
            except asyncio.CancelledError:
                raise
            except Exception:
                return False
            if result is not None and inspect.isawaitable(result):
                try:
                    await asyncio.wait_for(result, self.heartbeat_timeout)
                except asyncio.CancelledError:
                    raise
                except Exception:
                    return False
            return True

        return True

