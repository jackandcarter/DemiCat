import logging
from typing import List
from fastapi import HTTPException

from .schemas import EmbedDto, EmbedButtonDto

TITLE_LIMIT = 256
DESCRIPTION_LIMIT = 4096
FIELD_NAME_LIMIT = 256
FIELD_VALUE_LIMIT = 1024
FIELD_COUNT_LIMIT = 25
FOOTER_TEXT_LIMIT = 2048
AUTHOR_NAME_LIMIT = 256
TOTAL_CHAR_LIMIT = 6000
BUTTON_LABEL_LIMIT = 80
BUTTON_COUNT_LIMIT = 25
BUTTON_WIDTH_LIMIT = 200


def _check_url(name: str, url: str | None) -> None:
    if url and not url.lower().startswith(("http://", "https://")):
        logging.warning("Invalid URL scheme for %s: %s", name, url)
        raise HTTPException(422, detail=f"Invalid {name}")


def validate_embed_payload(dto: EmbedDto, buttons: List[EmbedButtonDto]) -> None:
    """Validate an embed payload against Discord's limits.

    Raises HTTPException(422) if any limit is violated.
    """
    total = 0

    if dto.title:
        if len(dto.title) > TITLE_LIMIT:
            logging.warning("Embed title exceeds %d characters", TITLE_LIMIT)
            raise HTTPException(422, detail="Title too long")
        total += len(dto.title)

    if dto.description:
        if len(dto.description) > DESCRIPTION_LIMIT:
            logging.warning("Embed description exceeds %d characters", DESCRIPTION_LIMIT)
            raise HTTPException(422, detail="Description too long")
        total += len(dto.description)

    if dto.footer_text:
        if len(dto.footer_text) > FOOTER_TEXT_LIMIT:
            logging.warning("Embed footer text exceeds %d characters", FOOTER_TEXT_LIMIT)
            raise HTTPException(422, detail="Footer too long")
        total += len(dto.footer_text)

    if dto.author_name:
        if len(dto.author_name) > AUTHOR_NAME_LIMIT:
            logging.warning("Embed author name exceeds %d characters", AUTHOR_NAME_LIMIT)
            raise HTTPException(422, detail="Author name too long")
        total += len(dto.author_name)

    if dto.fields:
        if len(dto.fields) > FIELD_COUNT_LIMIT:
            logging.warning("Embed has %d fields, limit is %d", len(dto.fields), FIELD_COUNT_LIMIT)
            raise HTTPException(422, detail="Too many fields")
        for field in dto.fields:
            if len(field.name) > FIELD_NAME_LIMIT:
                logging.warning("Field name exceeds %d characters", FIELD_NAME_LIMIT)
                raise HTTPException(422, detail="Field name too long")
            if len(field.value) > FIELD_VALUE_LIMIT:
                logging.warning("Field value exceeds %d characters", FIELD_VALUE_LIMIT)
                raise HTTPException(422, detail="Field value too long")
            total += len(field.name) + len(field.value)

    if total > TOTAL_CHAR_LIMIT:
        logging.warning("Embed totals %d characters, limit is %d", total, TOTAL_CHAR_LIMIT)
        raise HTTPException(422, detail="Embed too large")

    # Validate URLs on embed
    _check_url("url", dto.url)
    _check_url("thumbnail url", dto.thumbnail_url)
    _check_url("image url", dto.image_url)
    _check_url("provider url", dto.provider_url)
    _check_url("footer icon url", dto.footer_icon_url)
    _check_url("author icon url", dto.author_icon_url)
    _check_url("video url", dto.video_url)
    if dto.authors:
        for author in dto.authors:
            if author.name and len(author.name) > AUTHOR_NAME_LIMIT:
                logging.warning("Author name exceeds %d characters", AUTHOR_NAME_LIMIT)
                raise HTTPException(422, detail="Author name too long")
            _check_url("author url", author.url)
            _check_url("author icon url", author.icon_url)

    # Buttons
    if buttons:
        if len(buttons) > BUTTON_COUNT_LIMIT:
            logging.warning("Embed has %d buttons, limit is %d", len(buttons), BUTTON_COUNT_LIMIT)
            raise HTTPException(422, detail="Too many buttons")
        for btn in buttons:
            if len(btn.label) > BUTTON_LABEL_LIMIT:
                logging.warning("Button label exceeds %d characters", BUTTON_LABEL_LIMIT)
                raise HTTPException(422, detail="Button label too long")
            _check_url("button url", btn.url)
            width = btn.width or 1
            if width < 1 or width > BUTTON_WIDTH_LIMIT:
                logging.warning("Button width %d out of range", width)
                raise HTTPException(422, detail="Invalid button width")
