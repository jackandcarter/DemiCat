"""Helpers for validating uploaded files."""

from __future__ import annotations

from pathlib import Path

ALLOWED_IMAGE_CONTENT_TYPES: frozenset[str] = frozenset(
    {
        "image/png",
        "image/jpeg",
        "image/webp",
        "image/gif",
    }
)

ALLOWED_IMAGE_EXTENSIONS: frozenset[str] = frozenset(
    {".png", ".jpg", ".jpeg", ".webp", ".gif"}
)


def _normalize_content_type(content_type: str | None) -> str | None:
    """Return a normalised media type or ``None`` if missing."""

    if not content_type:
        return None
    base = content_type.split(";", 1)[0].strip().lower()
    if not base:
        return None
    if base == "image/jpg":
        return "image/jpeg"
    return base


def is_allowed_image_upload(filename: str | None, content_type: str | None) -> bool:
    """Return ``True`` when ``filename``/``content_type`` represent an allowed image."""

    normalised_type = _normalize_content_type(content_type)
    if normalised_type in ALLOWED_IMAGE_CONTENT_TYPES:
        return True
    if filename:
        ext = Path(filename).suffix.lower()
        if ext in ALLOWED_IMAGE_EXTENSIONS:
            return True
    return False


__all__ = [
    "ALLOWED_IMAGE_CONTENT_TYPES",
    "ALLOWED_IMAGE_EXTENSIONS",
    "is_allowed_image_upload",
]
