from __future__ import annotations

"""Configuration handling for DemiBot.

This module replaces the previous environment variable based configuration with a
JSON file that is automatically created on first run. The configuration file
stores database connection information, server options and the Discord bot
token. If any required value is missing the user will be prompted for it on
startup and the resulting configuration will be written back to disk with
permissions ``0o600`` to restrict access.
"""

from dataclasses import asdict, dataclass, field
from pathlib import Path
import json
import logging
import getpass
from urllib.parse import quote_plus

from sqlalchemy import text
from sqlalchemy.ext.asyncio import create_async_engine

CFG_PATH = Path.home() / ".config" / "demibot" / "config.json"


@dataclass
class ServerConfig:
    host: str = "127.0.0.1"
    port: int = 5050


@dataclass
class DBProfile:
    """Connection information for a single database profile."""

    host: str = "127.0.0.1"
    port: int = 3306
    database: str = "demibot"
    user: str = "demibot"
    password: str = "Admin"


@dataclass
class DatabaseConfig:
    """Database configuration containing local and remote profiles."""

    use_remote: bool = False
    local: DBProfile = field(default_factory=DBProfile)
    remote: DBProfile = field(default_factory=DBProfile)

    def active(self) -> DBProfile:
        return self.remote if self.use_remote else self.local

    @property
    def url(self) -> str:
        cfg = self.active()
        return (
            f"mysql+aiomysql://{quote_plus(cfg.user)}:{quote_plus(cfg.password)}"
            f"@{cfg.host}:{cfg.port}/{cfg.database}"
        )


@dataclass
class AppConfig:
    server: ServerConfig = field(default_factory=ServerConfig)
    database: DatabaseConfig = field(default_factory=DatabaseConfig)
    discord_token: str = ""
    dev_guild_id: int | None = None


def load_config() -> AppConfig:
    if CFG_PATH.exists():
        try:
            data = json.loads(CFG_PATH.read_text())
        except json.JSONDecodeError:
            logging.warning("Invalid JSON in %s, using defaults", CFG_PATH)
            return AppConfig()
        db_data = data.get("database", {})
        return AppConfig(
            server=ServerConfig(**data.get("server", {})),
            database=DatabaseConfig(
                use_remote=db_data.get("use_remote", False),
                local=DBProfile(**db_data.get("local", {})),
                remote=DBProfile(**db_data.get("remote", {})),
            ),
            discord_token=data.get("discord_token", ""),
            dev_guild_id=data.get("dev_guild_id"),
        )
    return AppConfig()


def save_config(cfg: AppConfig) -> None:
    data = {
        "server": asdict(cfg.server),
        "database": asdict(cfg.database),
        "discord_token": cfg.discord_token,
        "dev_guild_id": cfg.dev_guild_id,
    }
    CFG_PATH.parent.mkdir(parents=True, exist_ok=True)
    CFG_PATH.write_text(json.dumps(data, indent=2))
    try:
        CFG_PATH.chmod(0o600)
    except OSError as exc:  # pragma: no cover - platform dependent
        logging.warning("Unable to set permissions on %s: %s", CFG_PATH, exc)


async def ensure_config(force_reconfigure: bool = False) -> AppConfig:
    """Load configuration, prompting the user for any missing values.

    Parameters
    ----------
    force_reconfigure:
        If ``True`` all configuration values will be prompted for even if a
        ``config.json`` file already exists.
    """

    cfg = load_config()

    def _prompt_server() -> None:
        host = input(f"Server host [{cfg.server.host}]: ").strip()
        if host:
            cfg.server.host = host
        port = input(f"Server port [{cfg.server.port}]: ").strip()
        if port:
            cfg.server.port = int(port)

    def _prompt_profile(name: str, profile: DBProfile) -> None:
        """Prompt for basic connection details for a profile."""

        profile.host = input(f"{name} host [{profile.host}]: ") or profile.host
        port = input(f"{name} port [{profile.port}]: ") or profile.port
        profile.port = int(port)
        profile.database = (
            input(f"{name} database [{profile.database}]: ") or profile.database
        )

    def _prompt_database() -> None:
        """Prompt for database configuration including credentials."""

        _prompt_profile("Local", cfg.database.local)
        _prompt_profile("Remote", cfg.database.remote)
        resp = input("Use remote MySQL server? (y/N): ").strip().lower()
        cfg.database.use_remote = resp.startswith("y")

        profile = cfg.database.active()
        name = "Remote" if cfg.database.use_remote else "Local"
        profile.user = input(f"{name} username [{profile.user}]: ") or profile.user
        pwd = getpass.getpass(f"{name} password: ")
        if pwd:
            profile.password = pwd

    async def _check_database() -> bool:
        try:
            engine = create_async_engine(cfg.database.url, echo=False, future=True)
            async with engine.connect() as conn:
                await conn.execute(text("SELECT 1"))
            await engine.dispose()
            return True
        except Exception as exc:  # pragma: no cover - interactive prompt
            print(f"Database connection failed: {exc}")
            return False

    active_profile = cfg.database.active()
    # Reconfigure if any required credentials are missing
    needs_prompt = (
        force_reconfigure
        or not cfg.discord_token
        or not active_profile.user
        or not active_profile.password
    )

    if needs_prompt:
        _prompt_server()
        while True:
            _prompt_database()
            profile = cfg.database.active()
            if not profile.user or not profile.password:
                print("Username and password are required.")
                continue
            if await _check_database():
                break
        cfg.discord_token = (
            input(f"Enter Discord bot token [{cfg.discord_token}]: ").strip()
            or cfg.discord_token
        )

    save_config(cfg)
    return cfg
