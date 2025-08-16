from __future__ import annotations

from typing import AsyncGenerator

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
            Base.metadata.create_all(sync_conn)
            inspector = inspect(sync_conn)
            for table in Base.metadata.sorted_tables:
                existing = {c["name"] for c in inspector.get_columns(table.name)}
                for column in table.columns:
                    if column.name not in existing:
                        ddl = text(
                            f"ALTER TABLE {table.name} ADD COLUMN "
                            f"{column.compile(dialect=sync_conn.dialect)}"
                        )
                        sync_conn.execute(ddl)

        await conn.run_sync(_init)

    return _engine


async def get_session() -> AsyncGenerator[AsyncSession, None]:
    if _Session is None:
        raise RuntimeError("Engine not initialized")
    async with _Session() as session:
        yield session
