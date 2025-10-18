from __future__ import annotations

from typing import AsyncIterator

import asyncio
import logging
import os
from pathlib import Path

from alembic import command
from alembic.config import Config
from sqlalchemy.engine import make_url
from sqlalchemy.exc import OperationalError
from sqlalchemy.ext.asyncio import (
    AsyncEngine,
    AsyncSession,
    async_sessionmaker,
    create_async_engine,
)
from contextlib import asynccontextmanager

from .base import Base


_engine: AsyncEngine | None = None
_Session: async_sessionmaker[AsyncSession] | None = None
_engine_url: str | None = None
_init_lock = asyncio.Lock()
_DEBUG_SQL = os.getenv("DEMIBOT_DEBUG_SQLALCHEMY", "").lower() in {"1", "true", "yes"}


def _sync_url(url: str, *, hide_password: bool = True) -> str:
    """Convert an async SQLAlchemy URL to its sync counterpart.

    Alembic's migration runner uses synchronous SQLAlchemy engines.  This helper
    swaps out async drivers for their synchronous equivalents so the same DSN
    can be used for both async runtime access and migration execution.

    Parameters
    ----------
    url:
        The async SQLAlchemy URL.
    hide_password:
        When ``True`` (the default) the returned DSN masks embedded passwords
        using ``***`` so it is safe to log or expose in tests. Set to ``False``
        when the true credentials are required (for example when running
        migrations).
    """

    sa_url = make_url(url)
    driver = sa_url.drivername
    if driver.endswith("+aiosqlite"):
        driver = driver.replace("+aiosqlite", "")
    elif driver.endswith("+asyncpg"):
        driver = driver.replace("+asyncpg", "+psycopg2")
    elif driver.endswith("+asyncmy"):
        driver = driver.replace("+asyncmy", "+pymysql")
    elif driver.endswith("+aiomysql"):
        driver = driver.replace("+aiomysql", "+pymysql")

    sync_url = sa_url.set(drivername=driver)
    return sync_url.render_as_string(hide_password=hide_password)


async def init_db(url: str) -> AsyncEngine:
    """Create the database engine and run migrations for the given URL."""

    global _engine, _Session, _engine_url

    async with _init_lock:
        if _engine is not None and _engine_url == url:
            return _engine

        if _engine is not None:
            await _engine.dispose()
            _engine = None
            _Session = None
            _engine_url = None

        sa_url = make_url(url)
        sync_url = _sync_url(url, hide_password=False)
        masked_async = sa_url.render_as_string(hide_password=True)
        masked_sync = _sync_url(url)
        logging.debug("init_db async_url=%s sync_url=%s", masked_async, masked_sync)

        if sa_url.get_backend_name() == "sqlite":
            # SQLite lacks many ALTER TABLE capabilities used in migrations.
            # For test environments we create tables directly from metadata.
            _engine = create_async_engine(url, echo=_DEBUG_SQL, future=True)
            _Session = async_sessionmaker(_engine, expire_on_commit=False)
            async with _engine.begin() as conn:
                await conn.run_sync(Base.metadata.create_all)
            _engine_url = url
            return _engine

        # Run migrations using Alembic so the schema matches the latest models.
        config = Config()
        config.set_main_option(
            "script_location", str(Path(__file__).resolve().parent / "migrations")
        )
        config.set_main_option("sqlalchemy.url", sync_url)
        try:
            await asyncio.to_thread(command.upgrade, config, "head")
        except OperationalError as exc:  # pragma: no cover - requires real DB
            logging.error(
                "Migration failed for %s:%s as %s: %s",
                sa_url.host,
                sa_url.port,
                sa_url.username,
                exc,
            )
            raise

        _engine = create_async_engine(url, echo=_DEBUG_SQL, future=True)
        _Session = async_sessionmaker(_engine, expire_on_commit=False)
        _engine_url = url
        return _engine


@asynccontextmanager
async def get_session() -> AsyncIterator[AsyncSession]:
    if _Session is None:
        raise RuntimeError("database not initialized")
    async with _Session() as session:
        yield session
