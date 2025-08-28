from urllib.parse import quote_plus

import pytest
from sqlalchemy.ext.asyncio import create_async_engine


@pytest.mark.integration
def test_password_with_special_chars() -> None:
    """Engine parses credentials with special characters."""
    password = "p@ss/w:rd?123"
    url = f"mysql+aiomysql://user:{quote_plus(password)}@localhost/test"

    engine = create_async_engine(url)
    assert engine.url.password == password
    # Engine was never connected, but dispose to satisfy SQLAlchemy
    engine.sync_engine.dispose()


def test_sync_url_converts_aiomysql() -> None:
    """_sync_url converts aiomysql driver to a synchronous equivalent."""
    import sys
    import types
    from pathlib import Path

    root = Path(__file__).resolve().parents[1] / "demibot"
    sys.path.append(str(root))
    demibot_pkg = types.ModuleType("demibot")
    demibot_pkg.__path__ = [str(root / "demibot")]
    sys.modules.setdefault("demibot", demibot_pkg)

    from demibot.db.session import _sync_url

    url = "mysql+aiomysql://user:pass@localhost/test"
    assert _sync_url(url) == "mysql+pymysql://user:***@localhost/test"
