from __future__ import annotations

from typing import AsyncGenerator

import asyncio
from pathlib import Path

from alembic import command
from alembic.config import Config
from sqlalchemy.engine import make_url
from sqlalchemy.ext.asyncio import (
    AsyncEngine,
    AsyncSession,
    async_sessionmaker,
    create_async_engine,
)

from .base import Base


_engine: AsyncEngine | None = None
_Session: async_sessionmaker[AsyncSession] | None = None


def _sync_url(url: str) -> str:
    """Convert an async SQLAlchemy URL to its sync counterpart.

    Alembic's migration runner uses synchronous SQLAlchemy engines.  This helper
    swaps out async drivers for their synchronous equivalents so the same DSN
    can be used for both async runtime access and migration execution.
    """

    sa_url = make_url(url)
    driver = sa_url.drivername
    if driver.endswith("+aiosqlite"):
        driver = driver.replace("+aiosqlite", "")
    elif driver.endswith("+asyncpg"):
        driver = driver.replace("+asyncpg", "+psycopg2")
    elif driver.endswith("+asyncmy"):
        driver = driver.replace("+asyncmy", "+pymysql")
    return str(sa_url.set(drivername=driver))


async def init_db(url: str) -> AsyncEngine:
    """Create the database engine and run migrations for the given URL."""

    global _engine, _Session

    sa_url = make_url(url)
    sync_url = _sync_url(url)

    if sa_url.get_backend_name() == "sqlite":
        # SQLite lacks many ALTER TABLE capabilities used in migrations.
        # For test environments we create tables directly from metadata.
        _engine = create_async_engine(url, echo=False, future=True)
        _Session = async_sessionmaker(_engine, expire_on_commit=False)
        async with _engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        return _engine

    # Run migrations using Alembic so the schema matches the latest models.
    config = Config()
    config.set_main_option(
        "script_location", str(Path(__file__).resolve().parent / "migrations")
    )
    config.set_main_option("sqlalchemy.url", sync_url)
    await asyncio.to_thread(command.upgrade, config, "head")

    _engine = create_async_engine(url, echo=False, future=True)
    _Session = async_sessionmaker(_engine, expire_on_commit=False)
    return _engine


async def get_session() -> AsyncGenerator[AsyncSession, None]:
    if _Session is None:
        raise RuntimeError("Engine not initialized")
    async with _Session() as session:
        yield session
