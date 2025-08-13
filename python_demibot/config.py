import json
from pathlib import Path
from uuid import uuid4
import logging

logger = logging.getLogger(__name__)

CONFIG_PATH = Path(__file__).resolve().parent / "config.json"

# Default configuration structure
DEFAULT_CONFIG = {
    "mysql_host": "localhost",
    "mysql_user": "",
    "mysql_password": "",
    "mysql_db": "",
    "discord_token": "",
    "guild_id": "",
    "guild_name": "",
    "user_key": "",
    "sync_key": ""
}


def load_config() -> dict:
    """Load configuration from disk or run the setup wizard."""
    if not CONFIG_PATH.exists():
        return run_setup_wizard()
    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        return json.load(f)


def run_setup_wizard() -> dict:
    """Interactively prompt the user for configuration values."""
    cfg = DEFAULT_CONFIG.copy()
    logger.info("DemiBot initial setup - values will be saved to %s", CONFIG_PATH)
    cfg["mysql_host"] = input("MySQL host [localhost]: ") or "localhost"
    cfg["mysql_user"] = input("MySQL user: ")
    cfg["mysql_password"] = input("MySQL password: ")
    cfg["mysql_db"] = input("MySQL database: ")
    cfg["discord_token"] = input("Discord bot token: ")
    cfg["guild_id"] = input("Discord guild id: ")
    cfg["guild_name"] = input("Discord guild name: ")

    # Keys used for plugin authentication
    cfg["user_key"] = uuid4().hex
    cfg["sync_key"] = uuid4().hex

    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2)
    logger.info("Configuration saved.")
    return cfg
