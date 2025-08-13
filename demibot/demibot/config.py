from __future__ import annotations

import tomllib
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

from pydantic import BaseModel

CONFIG_PATH = Path.home() / ".demibot" / "config.toml"


class DiscordConfig(BaseModel):
    token: str


class ServerConfig(BaseModel):
    host: str = "127.0.0.1"
    port: int = 8123
    websocket_path: str = "/ws"


class DatabaseConfig(BaseModel):
    url: str


class SecurityConfig(BaseModel):
    require_api_key: bool = True


class AppConfig(BaseModel):
    discord: DiscordConfig
    server: ServerConfig
    database: DatabaseConfig
    security: SecurityConfig = SecurityConfig()


def load_config(path: Path = CONFIG_PATH) -> AppConfig:
    with path.open("rb") as f:
        data = tomllib.load(f)
    return AppConfig.model_validate(data)


def ensure_config(path: Path = CONFIG_PATH) -> AppConfig:
    """Load config or raise if missing. CLI wizard should create it."""
    if not path.exists():
        raise FileNotFoundError(
            f"Config not found at {path}. Run scripts/first_run_wizard.py"
        )
    return load_config(path)
