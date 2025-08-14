
from __future__ import annotations
from fastapi import FastAPI
from ..config import AppConfig
from .ws import websocket_endpoint
from .routes import channels, messages, officer_messages, users, embeds, events, interactions, validate_roles

def create_app(cfg: AppConfig) -> FastAPI:
    app = FastAPI(title="DemiBot API")

    # Regular routes
    app.include_router(validate_roles.router)
    app.include_router(channels.router)
    app.include_router(messages.router)
    app.include_router(officer_messages.router)
    app.include_router(users.router)
    app.include_router(embeds.router)
    app.include_router(events.router)
    app.include_router(interactions.router)

    # WebSocket
    app.add_api_websocket_route(cfg.server.websocket_path, websocket_endpoint)
    return app
