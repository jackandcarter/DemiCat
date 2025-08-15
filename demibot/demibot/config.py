
from __future__ import annotations
import os
from dataclasses import dataclass

@dataclass
class ServerConfig:
    host: str = os.environ.get("DEMIBOT_HOST", "0.0.0.0")
    port: int = int(os.environ.get("DEMIBOT_PORT", "8000"))
    websocket_path: str = os.environ.get("DEMIBOT_WS_PATH", "/ws")

@dataclass
class SecurityConfig:
    api_key: str = os.environ.get("DEMIBOT_API_KEY", "demo")


@dataclass
class DatabaseConfig:
    url: str = os.environ.get(
        "DEMIBOT_DB_URL", "sqlite+aiosqlite:///./demibot.db"
    )

@dataclass
class AppConfig:
    server: ServerConfig = ServerConfig()
    security: SecurityConfig = SecurityConfig()
    database: DatabaseConfig = DatabaseConfig()
