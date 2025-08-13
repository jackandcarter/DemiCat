from __future__ import annotations

from fastapi import FastAPI

from ..config import AppConfig
from ..db.session import get_session
from .routes import (
    channels,
    messages,
    officer_messages,
    users,
    embeds,
    events,
    interactions,
    validate_roles,
)
from .ws import websocket_endpoint


def create_app(cfg: AppConfig, engine) -> FastAPI:
    app = FastAPI(title="DemiBot API")
    app.include_router(validate_roles.router)
    app.include_router(channels.router)
    app.include_router(messages.router)
    app.include_router(officer_messages.router)
    app.include_router(users.router)
    app.include_router(embeds.router)
    app.include_router(events.router)
    app.include_router(interactions.router)

    app.add_api_websocket_route(cfg.server.websocket_path, websocket_endpoint)
    return app
