from __future__ import annotations

"""FastAPI application factory for DemiBot.

This module creates the HTTP API used by the DemiBot service.  All route
modules under :mod:`demibot.http.routes` expose an ``APIRouter`` instance named
``router``.  ``create_app`` dynamically imports each module and registers its
router with the FastAPI application.  This keeps the application setup
declarative and automatically includes any new route modules that are added in
the future.
"""

from importlib import import_module
import os
import pkgutil

from fastapi import FastAPI
from starlette.middleware.base import BaseHTTPMiddleware
from starlette.requests import Request

import structlog

from .ws import websocket_endpoint
from .ws_chat import websocket_endpoint_chat


logger = structlog.get_logger()


class RequestLoggingMiddleware(BaseHTTPMiddleware):
    """Log each request and whether it succeeds or fails.

    This middleware captures every HTTP request handled by the FastAPI app and
    writes a log line indicating the request method and path.  When the request
    completes, another line records the resulting status code.  If an exception
    occurs while processing the request, the exception is logged so that the
    failure reason is visible in the terminal and log files.
    """

    async def dispatch(self, request: Request, call_next):  # type: ignore[override]
        logger.info("request.start", method=request.method, path=request.url.path)
        try:
            response = await call_next(request)
            logger.info(
                "request.success",
                method=request.method,
                path=request.url.path,
                status_code=response.status_code,
            )
            return response
        except Exception as exc:  # pragma: no cover - defensive logging
            logger.exception(
                "request.failure",
                method=request.method,
                path=request.url.path,
                error=str(exc),
            )
            raise

def create_app() -> FastAPI:
    """Create and configure the FastAPI application."""

    app = FastAPI()
    # Attach middleware so every request logs its result.  This helps operators
    # see which communications succeed or fail in real time.
    app.add_middleware(RequestLoggingMiddleware)
    app.add_api_websocket_route("/ws/messages", websocket_endpoint)
    app.add_api_websocket_route("/ws/embeds", websocket_endpoint)
    app.add_api_websocket_route("/ws/templates", websocket_endpoint)
    app.add_api_websocket_route("/ws/officer-messages", websocket_endpoint)
    app.add_api_websocket_route("/ws/presences", websocket_endpoint)
    app.add_api_websocket_route("/ws/channels", websocket_endpoint)
    app.add_api_websocket_route("/ws/requests", websocket_endpoint)
    app.add_api_websocket_route("/ws/notepad", websocket_endpoint)
    app.add_api_websocket_route("/ws/chat", websocket_endpoint_chat)

    @app.get("/health")
    async def health() -> dict[str, str]:
        """Simple health check endpoint."""
        return {"status": "ok"}

    # Dynamically include routers from all modules in the routes package
    from . import routes as routes_pkg

    # Routes to skip entirely (removed features, experimental, etc.)
    excluded = {
        module_name.strip()
        for module_name in os.getenv("DISABLED_ROUTES", "syncshell").split(",")
        if module_name.strip()
    }
    loaded: list[str] = []
    skipped: list[str] = []

    for _, module_name, _ in pkgutil.iter_modules(routes_pkg.__path__):
        if module_name in excluded:
            skipped.append(module_name)
            continue
        try:
            module = import_module(f"{routes_pkg.__name__}.{module_name}")
        except Exception as exc:  # pragma: no cover - defensive startup logging
            # Log full traceback so we do not hide real bugs in route modules.
            logger.exception(
                "routes.import_failed module=%s error=%s", module_name, exc
            )
            continue

        router = getattr(module, "router", None)
        if router is not None:
            app.include_router(router)
            loaded.append(module_name)

    logger.info("routes.loaded %s", ",".join(loaded) or "-")
    if skipped:
        logger.info("routes.skipped %s", ",".join(skipped))

    return app

