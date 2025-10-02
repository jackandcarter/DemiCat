from urllib.parse import quote_plus

import pytest
from sqlalchemy.ext.asyncio import create_async_engine


def _bootstrap_demibot_package() -> None:
    import sys
    import types
    from pathlib import Path

    root = Path(__file__).resolve().parents[1] / "demibot"
    if str(root) not in sys.path:
        sys.path.append(str(root))
    if "demibot" not in sys.modules:
        demibot_pkg = types.ModuleType("demibot")
        demibot_pkg.__path__ = [str(root / "demibot")]
        sys.modules["demibot"] = demibot_pkg


@pytest.mark.integration
def test_password_with_special_chars() -> None:
    """Engine parses credentials with special characters."""
    password = "p@ss/w:rd?123"
    url = f"mysql+aiomysql://user:{quote_plus(password)}@127.0.0.1/test"

    engine = create_async_engine(url)
    assert engine.url.password == password
    # Engine was never connected, but dispose to satisfy SQLAlchemy
    engine.sync_engine.dispose()


def test_sync_url_converts_aiomysql() -> None:
    """_sync_url converts aiomysql driver to a synchronous equivalent."""
    _bootstrap_demibot_package()

    from demibot.db.session import _sync_url

    url = "mysql+aiomysql://user:pass@127.0.0.1/test"
    assert _sync_url(url) == "mysql+pymysql://user:***@127.0.0.1/test"


def test_database_config_appends_utf8mb4_charset() -> None:
    """DatabaseConfig.url always ensures utf8mb4 charset."""
    _bootstrap_demibot_package()

    from demibot.config import DatabaseConfig

    cfg = DatabaseConfig()
    assert cfg.url.endswith("?charset=utf8mb4")
