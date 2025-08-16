from __future__ import annotations

"""Minimal Flask application factory for DemiBot."""

from flask import Flask

from ..config import AppConfig


def create_app(cfg: AppConfig) -> Flask:
    app = Flask(__name__)

    @app.route("/health")
    def health() -> dict[str, str]:
        return {"status": "ok"}

    return app

