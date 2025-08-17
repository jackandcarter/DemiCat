from __future__ import annotations

from typing import AsyncGenerator

import logging
from sqlalchemy import inspect, text
from sqlalchemy.ext.asyncio import (
    AsyncEngine,
    AsyncSession,
    async_sessionmaker,
    create_async_engine,
)

from .base import Base

_engine: AsyncEngine | None = None
_Session: async_sessionmaker[AsyncSession] | None = None


async def init_db(url: str) -> AsyncEngine:
    """Create the database engine and ensure all tables and columns exist."""

    global _engine, _Session
    _engine = create_async_engine(url, echo=False, future=True)
    _Session = async_sessionmaker(_engine, expire_on_commit=False)

    async with _engine.begin() as conn:
        def _init(sync_conn):
            inspector = inspect(sync_conn)
            existing_tables = set(inspector.get_table_names())

            Base.metadata.create_all(sync_conn)
            inspector = inspect(sync_conn)

            for table in Base.metadata.sorted_tables:
                if table.name not in existing_tables:
                    logging.info("Created table %s", table.name)
                existing = {c["name"] for c in inspector.get_columns(table.name)}
                for column in table.columns:
                    if column.name not in existing:
                        ddl = text(
                            f"ALTER TABLE {table.name} ADD COLUMN "
                            f"{column.compile(dialect=sync_conn.dialect)}"
                        )
                        sync_conn.execute(ddl)
                        logging.info(
                            "Added column %s to table %s", column.name, table.name
                        )

        await conn.run_sync(_init)

    return _engine


async def get_session() -> AsyncGenerator[AsyncSession, None]:
    if _Session is None:
        raise RuntimeError("Engine not initialized")
    async with _Session() as session:
        yield session


def test_password_with_special_chars():
    """Ensure engines handle credentials with special characters."""

    from urllib.parse import quote_plus
    import asyncio

    password = "p@ss/w:rd?123"
    url = f"sqlite+aiosqlite:///:memory:?password={quote_plus(password)}"

    async def _check() -> None:
        engine = await init_db(url)
        async with engine.connect() as conn:
            result = await conn.execute(text("SELECT 1"))
            assert result.scalar_one() == 1
        await engine.dispose()

    asyncio.run(_check())
