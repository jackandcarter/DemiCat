from __future__ import annotations

from typing import AsyncGenerator

from sqlalchemy.ext.asyncio import AsyncEngine, AsyncSession, async_sessionmaker, create_async_engine

_engine: AsyncEngine | None = None
_Session: async_sessionmaker[AsyncSession] | None = None


def create_engine(url: str) -> AsyncEngine:
    global _engine, _Session
    _engine = create_async_engine(url, echo=False, future=True)
    _Session = async_sessionmaker(_engine, expire_on_commit=False)
    return _engine


async def get_session() -> AsyncGenerator[AsyncSession, None]:
    if _Session is None:
        raise RuntimeError("Engine not initialized")
    async with _Session() as session:
        yield session
