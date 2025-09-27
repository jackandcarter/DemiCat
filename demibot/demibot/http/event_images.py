"""In-memory library for resolving uploaded event images.

This module provides a lightweight registry that stores uploaded event assets
until they are referenced by an event payload.  The DemiCat plugin uploads
images ahead of time and only includes their ``imageId``/``thumbnailId`` in the
event creation request.  When the API receives that request it needs to resolve
the identifiers back into binary files so the assets can be sent to Discord as
message attachments.

The :class:`EventImageLibrary` keeps the uploaded data in memory which keeps the
implementation straightforward for tests while still mirroring the behaviour of
the production service that persists the data elsewhere.  The library is
designed to be concurrency safe and exposes helpers for storing, resolving, and
clearing entries.  Tests can interact with the shared
``event_image_library`` instance to seed fixtures without having to poke at
private attributes.
"""

from __future__ import annotations

from dataclasses import dataclass
import asyncio
from typing import Dict, Iterable, Optional


@dataclass
class EventImage:
    """Metadata and binary payload for an uploaded image."""

    id: str
    data: bytes
    filename: Optional[str] = None
    content_type: Optional[str] = None
    size: Optional[int] = None
    url: Optional[str] = None
    thumbnail_url: Optional[str] = None

    def read(self) -> bytes:
        """Return the image data as bytes."""

        return self.data


class EventImageLibrary:
    """Simple asynchronous registry for uploaded event images."""

    def __init__(self) -> None:
        self._images: Dict[str, EventImage] = {}
        self._lock = asyncio.Lock()

    async def store(self, image: EventImage) -> None:
        """Persist the provided :class:`EventImage` instance."""

        async with self._lock:
            self._images[image.id] = image

    async def store_bytes(
        self,
        image_id: str,
        *,
        data: bytes,
        filename: str | None = None,
        content_type: str | None = None,
        size: int | None = None,
        url: str | None = None,
        thumbnail_url: str | None = None,
    ) -> EventImage:
        """Create and store an :class:`EventImage` from raw bytes."""

        image = EventImage(
            id=image_id,
            data=data,
            filename=filename,
            content_type=content_type,
            size=size if size is not None else len(data),
            url=url,
            thumbnail_url=thumbnail_url,
        )
        await self.store(image)
        return image

    async def resolve(self, image_id: str) -> EventImage | None:
        """Return the stored image for ``image_id`` if present."""

        async with self._lock:
            return self._images.get(image_id)

    async def resolve_many(self, image_ids: Iterable[str]) -> Dict[str, EventImage]:
        """Resolve a batch of image identifiers."""

        async with self._lock:
            return {image_id: self._images[image_id] for image_id in image_ids if image_id in self._images}

    async def remove(self, image_id: str) -> None:
        """Remove a stored image when it is no longer required."""

        async with self._lock:
            self._images.pop(image_id, None)

    async def clear(self) -> None:
        """Remove all stored images."""

        async with self._lock:
            self._images.clear()


event_image_library = EventImageLibrary()

__all__ = ["EventImage", "EventImageLibrary", "event_image_library"]

