from __future__ import annotations

import json
import logging
from datetime import datetime
from typing import Iterable, Sequence

from fastapi import APIRouter, Depends, HTTPException, Response, status
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy.orm import selectinload

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import (
    NotepadStateDto,
    NotePageContentBody,
    NotePageCreateBody,
    NotePageDto,
    NotePageReorderBody,
    NotePageUpdateBody,
    NoteSectionCreateBody,
    NoteSectionDto,
    NoteSectionReorderBody,
    NoteSectionUpdateBody,
)
from ..ws import manager
from ...db.models import NotePage, NoteSection


router = APIRouter(prefix="/api")
logger = logging.getLogger(__name__)


def _require_officer(ctx: RequestContext) -> None:
    if "officer" not in ctx.roles:
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN, detail="officer role required"
        )


def _parse_int(value: str, label: str) -> int:
    try:
        return int(value)
    except (TypeError, ValueError) as exc:  # pragma: no cover - defensive
        raise HTTPException(status_code=400, detail=f"invalid {label}") from exc


async def _load_sections(
    db: AsyncSession, guild_id: int
) -> Sequence[NoteSection]:
    result = await db.execute(
        select(NoteSection)
        .options(selectinload(NoteSection.pages))
        .where(NoteSection.guild_id == guild_id)
        .order_by(NoteSection.sort_order, NoteSection.id)
    )
    return result.scalars().unique().all()


def _serialize_note_page(page: NotePage) -> NotePageDto:
    return NotePageDto(
        id=str(page.id),
        section_id=str(page.section_id),
        title=page.title,
        content=page.content or "",
        order=page.sort_order,
        color=page.color,
        created_by_id=str(page.created_by_id) if page.created_by_id is not None else None,
        updated_by_id=str(page.updated_by_id) if page.updated_by_id is not None else None,
        created_at=page.created_at,
        updated_at=page.updated_at,
        version=page.version,
    )


def _serialize_note_section(section: NoteSection) -> NoteSectionDto:
    raw_pages = section.__dict__.get("pages")
    if raw_pages is None:
        raw_pages = []
    pages = [
        _serialize_note_page(page)
        for page in sorted(
            (p for p in raw_pages if not p.is_deleted),
            key=lambda p: (p.sort_order, p.id),
        )
    ]
    return NoteSectionDto(
        id=str(section.id),
        name=section.name,
        order=section.sort_order,
        color=section.color,
        created_by_id=
        str(section.created_by_id) if section.created_by_id is not None else None,
        updated_by_id=
        str(section.updated_by_id) if section.updated_by_id is not None else None,
        created_at=section.created_at,
        updated_at=section.updated_at,
        version=section.version,
        pages=pages,
    )


def _serialize_notepad_state(sections: Iterable[NoteSection]) -> NotepadStateDto:
    active_sections = [
        _serialize_note_section(section)
        for section in sections
        if not section.is_deleted
    ]
    return NotepadStateDto(sections=active_sections)


async def _broadcast_notepad_event(
    ctx: RequestContext, topic: str, payload: dict
) -> None:
    message = json.dumps(
        {"topic": topic, "payload": payload},
        ensure_ascii=False,
    )
    await manager.broadcast_text(message, ctx.guild.id, path="/ws/notepad")


async def _get_section(
    db: AsyncSession, guild_id: int, section_id: int, include_deleted: bool = False
) -> NoteSection:
    result = await db.execute(
        select(NoteSection)
        .options(selectinload(NoteSection.pages))
        .where(NoteSection.id == section_id)
    )
    section = result.scalar_one_or_none()
    if (
        section is None
        or section.guild_id != guild_id
        or (section.is_deleted and not include_deleted)
    ):
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="section not found")
    return section


async def _get_page(
    db: AsyncSession, guild_id: int, page_id: int, include_deleted: bool = False
) -> NotePage:
    page = await db.get(NotePage, page_id)
    if (
        page is None
        or page.guild_id != guild_id
        or (page.is_deleted and not include_deleted)
    ):
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="page not found")
    return page


@router.get("/notepad")
async def list_notepad(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> NotepadStateDto:
    sections = await _load_sections(db, ctx.guild.id)
    return _serialize_notepad_state(sections)


@router.post("/notepad/sections")
async def create_section(
    body: NoteSectionCreateBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> NoteSectionDto:
    _require_officer(ctx)
    max_order = await db.scalar(
        select(func.max(NoteSection.sort_order)).where(
            NoteSection.guild_id == ctx.guild.id,
            NoteSection.is_deleted.is_(False),
        )
    )
    next_order = (max_order if max_order is not None else -1) + 1
    section = NoteSection(
        guild_id=ctx.guild.id,
        name=body.name,
        color=body.color,
        sort_order=next_order,
        created_by_id=ctx.user.id,
        updated_by_id=ctx.user.id,
    )
    db.add(section)
    await db.commit()
    await db.refresh(section)
    dto = _serialize_note_section(section)
    await _broadcast_notepad_event(
        ctx,
        "notepad.section.created",
        {"section": dto.model_dump(mode="json", by_alias=True)},
    )
    return dto


@router.patch("/notepad/sections/{section_id}")
async def update_section(
    section_id: str,
    body: NoteSectionUpdateBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> NoteSectionDto:
    _require_officer(ctx)
    sid = _parse_int(section_id, "sectionId")
    section = await _get_section(db, ctx.guild.id, sid)
    if section.version != body.version:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="version conflict")
    if body.name is not None:
        section.name = body.name
    if body.color is not None:
        section.color = body.color
    section.version += 1
    section.updated_by_id = ctx.user.id
    await db.commit()
    await db.refresh(section)
    dto = _serialize_note_section(section)
    await _broadcast_notepad_event(
        ctx,
        "notepad.section.updated",
        {"section": dto.model_dump(mode="json", by_alias=True)},
    )
    return dto


@router.post("/notepad/sections/reorder")
async def reorder_sections(
    body: NoteSectionReorderBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> NotepadStateDto:
    _require_officer(ctx)
    requested_ids = [_parse_int(section_id, "sectionId") for section_id in body.section_ids]
    if len(requested_ids) != len(set(requested_ids)):
        raise HTTPException(status_code=400, detail="duplicate section ids")
    sections = await _load_sections(db, ctx.guild.id)
    section_map = {section.id: section for section in sections if not section.is_deleted}
    missing = [sid for sid in requested_ids if sid not in section_map]
    if missing:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="section not found")
    remaining = [
        section
        for section in sections
        if section.id not in requested_ids and not section.is_deleted
    ]
    ordered_sections = [section_map[sid] for sid in requested_ids]
    ordered_sections.extend(sorted(remaining, key=lambda s: (s.sort_order, s.id)))

    deleted_sections = [
        section for section in sections if section.is_deleted
    ]
    deleted_sections.sort(key=lambda s: (s.sort_order, s.id))

    final_sections: list[NoteSection] = []
    final_sections.extend(ordered_sections)
    final_sections.extend(deleted_sections)

    temp_updates: list[tuple[NoteSection, int]] = []
    total = len(final_sections)
    for index, section in enumerate(final_sections):
        if section.sort_order != index:
            section.sort_order = total + index
            temp_updates.append((section, index))
    if temp_updates:
        await db.flush()
        for section, index in temp_updates:
            section.sort_order = index
            section.version += 1
            section.updated_by_id = ctx.user.id
    await db.commit()
    sections = await _load_sections(db, ctx.guild.id)
    state = _serialize_notepad_state(sections)
    await _broadcast_notepad_event(
        ctx,
        "notepad.section.reordered",
        {"sections": state.model_dump(mode="json", by_alias=True)["sections"]},
    )
    return state


@router.delete("/notepad/sections/{section_id}", status_code=status.HTTP_204_NO_CONTENT)
async def delete_section(
    section_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> Response:
    _require_officer(ctx)
    sid = _parse_int(section_id, "sectionId")
    section = await _get_section(db, ctx.guild.id, sid)
    now = datetime.utcnow()
    raw_pages = section.__dict__.get("pages") or []
    if not section.is_deleted:
        section.is_deleted = True
        section.deleted_at = now
        section.version += 1
        section.updated_by_id = ctx.user.id
    for page in raw_pages:
        if not page.is_deleted:
            page.is_deleted = True
            page.deleted_at = now
            page.version += 1
            page.updated_by_id = ctx.user.id
    await db.commit()
    await _broadcast_notepad_event(
        ctx,
        "notepad.section.deleted",
        {"sectionId": str(section.id)},
    )
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.post("/notepad/pages")
async def create_page(
    body: NotePageCreateBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> NotePageDto:
    _require_officer(ctx)
    section_id = _parse_int(body.section_id, "sectionId")
    section = await _get_section(db, ctx.guild.id, section_id)
    max_order = await db.scalar(
        select(func.max(NotePage.sort_order)).where(
            NotePage.section_id == section.id,
            NotePage.is_deleted.is_(False),
        )
    )
    next_order = (max_order if max_order is not None else -1) + 1
    page = NotePage(
        guild_id=ctx.guild.id,
        section_id=section.id,
        title=body.title,
        content=body.content,
        color=body.color,
        sort_order=next_order,
        created_by_id=ctx.user.id,
        updated_by_id=ctx.user.id,
    )
    db.add(page)
    await db.commit()
    await db.refresh(page)
    dto = _serialize_note_page(page)
    await _broadcast_notepad_event(
        ctx,
        "notepad.page.created",
        {"page": dto.model_dump(mode="json", by_alias=True)},
    )
    return dto


@router.patch("/notepad/pages/{page_id}")
async def update_page(
    page_id: str,
    body: NotePageUpdateBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> NotePageDto:
    _require_officer(ctx)
    pid = _parse_int(page_id, "pageId")
    page = await _get_page(db, ctx.guild.id, pid)
    if page.version != body.version:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="version conflict")
    if body.title is not None:
        page.title = body.title
    if body.color is not None:
        page.color = body.color
    page.version += 1
    page.updated_by_id = ctx.user.id
    await db.commit()
    await db.refresh(page)
    dto = _serialize_note_page(page)
    await _broadcast_notepad_event(
        ctx,
        "notepad.page.updated",
        {"page": dto.model_dump(mode="json", by_alias=True)},
    )
    return dto


@router.patch("/notepad/pages/{page_id}/content")
async def update_page_content(
    page_id: str,
    body: NotePageContentBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> NotePageDto:
    _require_officer(ctx)
    pid = _parse_int(page_id, "pageId")
    page = await _get_page(db, ctx.guild.id, pid)
    if page.version != body.version:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="version conflict")
    page.content = body.content
    page.version += 1
    page.updated_by_id = ctx.user.id
    await db.commit()
    await db.refresh(page)
    dto = _serialize_note_page(page)
    await _broadcast_notepad_event(
        ctx,
        "notepad.page.updated",
        {"page": dto.model_dump(mode="json", by_alias=True)},
    )
    return dto


@router.post("/notepad/pages/reorder")
async def reorder_pages(
    body: NotePageReorderBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> NotepadStateDto:
    _require_officer(ctx)
    provided_section_ids: list[int] = []
    provided_page_ids: list[int] = []
    for entry in body.sections:
        sid = _parse_int(entry.section_id, "sectionId")
        provided_section_ids.append(sid)
        for pid_str in entry.page_ids:
            provided_page_ids.append(_parse_int(pid_str, "pageId"))
    if len(provided_page_ids) != len(set(provided_page_ids)):
        raise HTTPException(status_code=400, detail="duplicate page ids")

    sections = await _load_sections(db, ctx.guild.id)
    section_map = {section.id: section for section in sections if not section.is_deleted}
    missing_sections = [sid for sid in provided_section_ids if sid not in section_map]
    if missing_sections:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="section not found")

    result = await db.execute(
        select(NotePage).where(
            NotePage.guild_id == ctx.guild.id,
            NotePage.is_deleted.is_(False),
        )
    )
    pages = result.scalars().all()
    page_map = {page.id: page for page in pages}
    missing_pages = [pid for pid in provided_page_ids if pid not in page_map]
    if missing_pages:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="page not found")

    final_orders: dict[int, list[int]] = {}
    for entry in body.sections:
        sid = _parse_int(entry.section_id, "sectionId")
        final_orders[sid] = [_parse_int(pid, "pageId") for pid in entry.page_ids]

    for page in sorted(pages, key=lambda p: (p.section_id, p.sort_order, p.id)):
        if page.id in provided_page_ids:
            continue
        final_orders.setdefault(page.section_id, []).append(page.id)

    affected_section_ids = {sid for sid in final_orders if sid in section_map}
    deleted_pages: list[NotePage] = []
    if affected_section_ids:
        result = await db.execute(
            select(NotePage).where(
                NotePage.guild_id == ctx.guild.id,
                NotePage.section_id.in_(affected_section_ids),
                NotePage.is_deleted.is_(True),
            )
        )
        deleted_pages = result.scalars().all()

    max_sort_order = await db.scalar(
        select(func.max(NotePage.sort_order)).where(NotePage.guild_id == ctx.guild.id)
    )
    next_temp_order = (max_sort_order if max_sort_order is not None else -1) + 1

    temp_page_updates: list[tuple[NotePage, int]] = []
    for section_id, ids in final_orders.items():
        if section_id not in section_map:
            continue
        for index, page_id in enumerate(ids):
            page = page_map[page_id]
            changed = False
            if page.section_id != section_id:
                page.section_id = section_id
                page.section = section_map[section_id]
                changed = True
            if page.sort_order != index:
                changed = True
            if changed:
                page.sort_order = next_temp_order
                next_temp_order += 1
                temp_page_updates.append((page, index))

    deleted_offsets: dict[int, int] = {}
    for page in sorted(deleted_pages, key=lambda p: (p.section_id, p.sort_order, p.id)):
        active_count = len(final_orders.get(page.section_id, []))
        offset = deleted_offsets.get(page.section_id, 0)
        deleted_offsets[page.section_id] = offset + 1
        target_index = active_count + offset
        if page.sort_order != target_index:
            page.sort_order = next_temp_order
            next_temp_order += 1
            temp_page_updates.append((page, target_index))

    if temp_page_updates:
        await db.flush()
        for page, index in temp_page_updates:
            page.sort_order = index
            page.version += 1
            page.updated_by_id = ctx.user.id
    await db.commit()
    sections = await _load_sections(db, ctx.guild.id)
    state = _serialize_notepad_state(sections)
    await _broadcast_notepad_event(
        ctx,
        "notepad.page.reordered",
        {"sections": state.model_dump(mode="json", by_alias=True)["sections"]},
    )
    return state


@router.delete("/notepad/pages/{page_id}", status_code=status.HTTP_204_NO_CONTENT)
async def delete_page(
    page_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> Response:
    _require_officer(ctx)
    pid = _parse_int(page_id, "pageId")
    page = await _get_page(db, ctx.guild.id, pid)
    now = datetime.utcnow()
    if not page.is_deleted:
        page.is_deleted = True
        page.deleted_at = now
        page.version += 1
        page.updated_by_id = ctx.user.id
    await db.commit()
    await _broadcast_notepad_event(
        ctx,
        "notepad.page.deleted",
        {"pageId": str(page.id), "sectionId": str(page.section_id)},
    )
    return Response(status_code=status.HTTP_204_NO_CONTENT)
