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
import pkgutil

from fastapi import FastAPI

from .ws import websocket_endpoint

from typing import TYPE_CHECKING

if TYPE_CHECKING:  # pragma: no cover - imported for type hints only
    from ..config import AppConfig


def create_app(cfg: "AppConfig | None") -> FastAPI:
    """Create and configure the FastAPI application."""

    app = FastAPI()
    app.add_api_websocket_route("/ws/messages", websocket_endpoint)
    app.add_api_websocket_route("/ws/embeds", websocket_endpoint)
    app.add_api_websocket_route("/ws/officer-messages", websocket_endpoint)
    app.add_api_websocket_route("/ws/presences", websocket_endpoint)
    app.add_api_websocket_route("/ws/channels", websocket_endpoint)

    @app.get("/health")
    async def health() -> dict[str, str]:
        """Simple health check endpoint."""
        return {"status": "ok"}

    # Dynamically include routers from all modules in the routes package
    from . import routes as routes_pkg

    for _, module_name, _ in pkgutil.iter_modules(routes_pkg.__path__):
        module = import_module(f"{routes_pkg.__name__}.{module_name}")
        router = getattr(module, "router", None)
        if router is not None:
            app.include_router(router)

    return app

