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
