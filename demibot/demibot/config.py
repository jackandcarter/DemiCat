from __future__ import annotations

"""Configuration handling for DemiBot.

This module replaces the previous environment variable based configuration with a
JSON file that is automatically created on first run. The configuration file
stores database connection information, server options and the Discord bot
token. If any required value is missing the user will be prompted for it on
startup and the resulting configuration will be written back to disk with
permissions ``0o600`` to restrict access.
"""

from dataclasses import asdict, dataclass
from pathlib import Path
import asyncio
import json
import logging

from sqlalchemy import text
from sqlalchemy.ext.asyncio import create_async_engine

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
        try:
            data = json.loads(CFG_PATH.read_text())
        except json.JSONDecodeError:
            logging.warning("Invalid JSON in %s, using defaults", CFG_PATH)
            return AppConfig()
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
    try:
        CFG_PATH.chmod(0o600)
    except OSError as exc:  # pragma: no cover - platform dependent
        logging.warning("Unable to set permissions on %s: %s", CFG_PATH, exc)


def ensure_config(force_reconfigure: bool = False) -> AppConfig:
    """Load configuration, prompting the user for any missing values.

    Parameters
    ----------
    force_reconfigure:
        If ``True`` all configuration values will be prompted for even if a
        ``config.json`` file already exists.
    """

    cfg = load_config() if CFG_PATH.exists() else AppConfig()
    changed = False

    def _prompt_database() -> None:
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

    def _check_database() -> bool:
        try:
            async def _test() -> None:
                engine = create_async_engine(cfg.database.url, echo=False, future=True)
                async with engine.connect() as conn:
                    await conn.execute(text("SELECT 1"))
                await engine.dispose()

            asyncio.run(_test())
            return True
        except Exception as exc:  # pragma: no cover - interactive prompt
            print(f"Database connection failed: {exc}")
            return False

    needs_prompt = force_reconfigure or not CFG_PATH.exists()

    if needs_prompt:
        while True:
            _prompt_database()
            if _check_database():
                break
        cfg.discord_token = (
            input(f"Enter Discord bot token [{cfg.discord_token}]: ").strip()
            or cfg.discord_token
        )
        changed = True
    else:
        mode = "remote" if cfg.database.use_remote else "local"
        print(
            f"Using {mode} MySQL database at "
            f"{cfg.database.host}:{cfg.database.port}/{cfg.database.database}"
        )
        if not _check_database():
            print("Unable to connect. Reconfiguring...")
            while True:
                _prompt_database()
                if _check_database():
                    break
            cfg.discord_token = (
                input(f"Enter Discord bot token [{cfg.discord_token}]: ").strip()
                or cfg.discord_token
            )
            changed = True
        elif not cfg.discord_token:
            cfg.discord_token = input("Enter Discord bot token: ").strip()
            changed = True

    if changed:
        save_config(cfg)
    return cfg
