from __future__ import annotations

from pathlib import Path

from demibot.config import CONFIG_PATH, AppConfig, DatabaseConfig, DiscordConfig, SecurityConfig, ServerConfig


def run_wizard() -> None:
    token = input("Discord bot token: ")
    db_url = input("Database URL [mysql+aiomysql://demibot:Admin@127.0.0.1:3306/demibot]: ") or "mysql+aiomysql://demibot:Admin@127.0.0.1:3306/demibot"
    host = input("HTTP host [127.0.0.1]: ") or "127.0.0.1"
    port = int(input("HTTP port [8123]: ") or "8123")

    cfg = AppConfig(
        discord=DiscordConfig(token=token),
        server=ServerConfig(host=host, port=port),
        database=DatabaseConfig(url=db_url),
        security=SecurityConfig(),
    )

    CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
    CONFIG_PATH.write_text(cfg.model_dump_json(indent=2))
    print(f"Wrote config to {CONFIG_PATH}")


if __name__ == "__main__":
    run_wizard()
