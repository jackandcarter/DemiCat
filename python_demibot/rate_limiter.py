from __future__ import annotations

import asyncio
from collections import deque
from typing import Awaitable, Callable, Deque, Tuple, TypeVar

T = TypeVar("T")

queue: Deque[Tuple[Callable[[], Awaitable[T]], asyncio.Future]] = deque()
processing = False

async def _process_queue() -> None:
    global processing
    if processing or not queue:
        return
    processing = True
    fn, fut = queue.popleft()
    try:
        result = await fn()
        fut.set_result(result)
    except Exception as exc:  # pragma: no cover - defensive
        fut.set_exception(exc)
    finally:
        await asyncio.sleep(1)
        processing = False
        asyncio.create_task(_process_queue())

async def enqueue(fn: Callable[[], Awaitable[T]]) -> T:
    """Run ``fn`` after waiting for earlier tasks."""
    loop = asyncio.get_running_loop()
    fut: asyncio.Future = loop.create_future()
    queue.append((fn, fut))
    await _process_queue()
    return await fut
