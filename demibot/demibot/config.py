from __future__ import annotations

"""Configuration handling for DemiBot.

This module replaces the previous environment variable based configuration with a
JSON file that is automatically created on first run.  The configuration file
stores database connection information, server options and the Discord bot
token.  If any required value is missing the user will be prompted for it on
startup and the resulting configuration will be written back to disk.
"""

from dataclasses import asdict, dataclass
from pathlib import Path
import json

CFG_PATH = Path(__file__).with_name("config.json")


@dataclass
class ServerConfig:
    host: str = "0.0.0.0"
    port: int = 5000


@dataclass
class DatabaseConfig:
    """Database configuration supporting local or remote MySQL instances."""

    use_remote: bool = False
    host: str = "localhost"
    port: int = 3306
    database: str = "demibot"
    user: str = "root"
    password: str = ""

    @property
    def url(self) -> str:
        return (
            f"mysql+aiomysql://{self.user}:{self.password}"
            f"@{self.host}:{self.port}/{self.database}"
        )


@dataclass
class AppConfig:
    server: ServerConfig = ServerConfig()
    database: DatabaseConfig = DatabaseConfig()
    discord_token: str = ""


def load_config() -> AppConfig:
    if CFG_PATH.exists():
        data = json.loads(CFG_PATH.read_text())
        return AppConfig(
            server=ServerConfig(**data.get("server", {})),
            database=DatabaseConfig(**data.get("database", {})),
            discord_token=data.get("discord_token", ""),
        )
    return AppConfig()


def save_config(cfg: AppConfig) -> None:
    data = {
        "server": asdict(cfg.server),
        "database": asdict(cfg.database),
        "discord_token": cfg.discord_token,
    }
    CFG_PATH.write_text(json.dumps(data, indent=2))


def ensure_config() -> AppConfig:
    """Load configuration, prompting the user for any missing values."""

    cfg = load_config()
    changed = False

    # Determine database location
    if CFG_PATH.exists():
        mode = "remote" if cfg.database.use_remote else "local"
        print(
            f"Using {mode} MySQL database at "
            f"{cfg.database.host}:{cfg.database.port}/{cfg.database.database}"
        )
    else:
        resp = input("Use remote MySQL server? (y/N): ").strip().lower()
        cfg.database.use_remote = resp.startswith("y")
        if cfg.database.use_remote:
            cfg.database.host = (
                input(f"Remote host [{cfg.database.host}]: ") or cfg.database.host
            )
            port = input(f"Remote port [{cfg.database.port}]: ") or cfg.database.port
            cfg.database.port = int(port)
            cfg.database.database = (
                input(f"Database name [{cfg.database.database}]: ")
                or cfg.database.database
            )
        changed = True

    if not cfg.discord_token:
        cfg.discord_token = input("Enter Discord bot token: ").strip()
        changed = True

    if changed:
        save_config(cfg)
    return cfg

