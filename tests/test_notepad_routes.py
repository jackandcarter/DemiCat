import json
from pathlib import Path
from types import SimpleNamespace

import pytest
from fastapi import HTTPException
from sqlalchemy import select

from demibot.db.models import Guild, NoteSection, User
from demibot.db.session import get_session, init_db
from demibot.http.routes import notepad
from demibot.http.schemas import (
    NotePageContentBody,
    NotePageCreateBody,
    NotePageReorderBody,
    NotePageReorderEntry,
    NotePageUpdateBody,
    NoteSectionCreateBody,
    NoteSectionReorderBody,
    NoteSectionUpdateBody,
)


class StubContext(SimpleNamespace):
    pass


@pytest.fixture
def anyio_backend():
    return "asyncio"


@pytest.mark.anyio
async def test_notepad_crud_flow(tmp_path, monkeypatch):
    db_path = Path(tmp_path) / "notepad.db"
    await init_db(f"sqlite+aiosqlite:///{db_path}")

    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Guild")
        user = User(id=1, discord_user_id=10, global_name="Officer")
        db.add_all([guild, user])
        await db.commit()

    ctx_officer = StubContext(
        guild=SimpleNamespace(id=1),
        user=SimpleNamespace(id=1),
        roles=["officer"],
    )
    ctx_member = StubContext(
        guild=SimpleNamespace(id=1),
        user=SimpleNamespace(id=1),
        roles=[],
    )

    events: list[tuple[dict, int, str]] = []

    async def fake_broadcast(message: str, guild_id: int, path: str):
        events.append((json.loads(message), guild_id, path))

    monkeypatch.setattr(notepad.manager, "broadcast_text", fake_broadcast)

    async with get_session() as db:
        state = await notepad.list_notepad(ctx=ctx_member, db=db)
        assert state.sections == []

    async with get_session() as db:
        with pytest.raises(HTTPException) as excinfo:
            await notepad.create_section(
                body=NoteSectionCreateBody(name="Member"), ctx=ctx_member, db=db
            )
        assert excinfo.value.status_code == 403

    async with get_session() as db:
        section = await notepad.create_section(
            body=NoteSectionCreateBody(name="Raid", color=123456),
            ctx=ctx_officer,
            db=db,
        )
    assert section.name == "Raid"
    assert section.color == 123456

    async with get_session() as db:
        renamed = await notepad.update_section(
            section_id=section.id,
            body=NoteSectionUpdateBody(name="Raid Alpha", version=section.version),
            ctx=ctx_officer,
            db=db,
        )
    assert renamed.name == "Raid Alpha"
    assert renamed.version == section.version + 1

    async with get_session() as db:
        with pytest.raises(HTTPException) as excinfo:
            await notepad.update_section(
                section_id=section.id,
                body=NoteSectionUpdateBody(name="oops", version=section.version),
                ctx=ctx_officer,
                db=db,
            )
        assert excinfo.value.status_code == 409

    async with get_session() as db:
        state = await notepad.list_notepad(ctx=ctx_member, db=db)
    assert len(state.sections) == 1
    section_id = state.sections[0].id

    async with get_session() as db:
        page = await notepad.create_page(
            body=NotePageCreateBody(sectionId=section_id, title="Notes", content="A"),
            ctx=ctx_officer,
            db=db,
        )
    assert page.content == "A"

    async with get_session() as db:
        updated_page = await notepad.update_page_content(
            page_id=page.id,
            body=NotePageContentBody(content="Updated", version=page.version),
            ctx=ctx_officer,
            db=db,
        )
    assert updated_page.version == page.version + 1

    async with get_session() as db:
        with pytest.raises(HTTPException) as excinfo:
            await notepad.update_page_content(
                page_id=page.id,
                body=NotePageContentBody(content="stale", version=page.version),
                ctx=ctx_officer,
                db=db,
            )
        assert excinfo.value.status_code == 409

    async with get_session() as db:
        updated_meta = await notepad.update_page(
            page_id=page.id,
            body=NotePageUpdateBody(title="New Title", version=updated_page.version),
            ctx=ctx_officer,
            db=db,
        )
    assert updated_meta.title == "New Title"
    assert updated_meta.version == updated_page.version + 1

    async with get_session() as db:
        section2 = await notepad.create_section(
            body=NoteSectionCreateBody(name="Logs"), ctx=ctx_officer, db=db
        )

    async with get_session() as db:
        state = await notepad.reorder_sections(
            body=NoteSectionReorderBody(sectionIds=[section2.id, section.id]),
            ctx=ctx_officer,
            db=db,
        )
    assert [s.id for s in state.sections][:2] == [section2.id, section.id]

    async with get_session() as db:
        page2 = await notepad.create_page(
            body=NotePageCreateBody(sectionId=section.id, title="Other", content="B"),
            ctx=ctx_officer,
            db=db,
        )

    reorder_body = NotePageReorderBody(
        sections=[
            NotePageReorderEntry(sectionId=section2.id, pageIds=[page2.id]),
            NotePageReorderEntry(sectionId=section.id, pageIds=[page.id]),
        ]
    )
    async with get_session() as db:
        state = await notepad.reorder_pages(
            body=reorder_body,
            ctx=ctx_officer,
            db=db,
        )
    sections_by_id = {s.id: s for s in state.sections}
    assert sections_by_id[section2.id].pages[0].id == page2.id
    assert sections_by_id[section.id].pages[0].id == page.id

    async with get_session() as db:
        await notepad.delete_page(page_id=page2.id, ctx=ctx_officer, db=db)

    reorder_after_delete = NotePageReorderBody(
        sections=[
            NotePageReorderEntry(sectionId=section2.id, pageIds=[]),
            NotePageReorderEntry(sectionId=section.id, pageIds=[page.id]),
        ]
    )
    async with get_session() as db:
        post_delete_state = await notepad.reorder_pages(
            body=reorder_after_delete,
            ctx=ctx_officer,
            db=db,
        )
    sections_after_delete = {s.id: s for s in post_delete_state.sections}
    assert sections_after_delete[section.id].pages[0].id == page.id
    assert sections_after_delete[section2.id].pages == []

    async with get_session() as db:
        await notepad.delete_section(section_id=section.id, ctx=ctx_officer, db=db)

    async with get_session() as db:
        state = await notepad.list_notepad(ctx=ctx_member, db=db)
    returned_ids = {s.id for s in state.sections}
    assert section.id not in returned_ids

    topics = {event[0]["topic"] for event in events}
    assert {
        "notepad.section.created",
        "notepad.page.created",
        "notepad.page.updated",
        "notepad.section.reordered",
        "notepad.page.reordered",
        "notepad.page.deleted",
        "notepad.section.deleted",
    }.issubset(topics)
    assert all(event[2] == "/ws/notepad" for event in events)


@pytest.mark.anyio
async def test_reorder_sections_with_soft_deleted(tmp_path, monkeypatch):
    db_path = Path(tmp_path) / "notepad_deleted.db"
    await init_db(f"sqlite+aiosqlite:///{db_path}")

    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Guild")
        user = User(id=1, discord_user_id=10, global_name="Officer")
        db.add_all([guild, user])
        await db.commit()

    ctx_officer = StubContext(
        guild=SimpleNamespace(id=1),
        user=SimpleNamespace(id=1),
        roles=["officer"],
    )

    events: list[tuple[dict, int, str]] = []

    async def fake_broadcast(message: str, guild_id: int, path: str):
        events.append((json.loads(message), guild_id, path))

    monkeypatch.setattr(notepad.manager, "broadcast_text", fake_broadcast)

    async with get_session() as db:
        section_one = await notepad.create_section(
            body=NoteSectionCreateBody(name="Alpha"), ctx=ctx_officer, db=db
        )
        section_two = await notepad.create_section(
            body=NoteSectionCreateBody(name="Beta"), ctx=ctx_officer, db=db
        )
        section_three = await notepad.create_section(
            body=NoteSectionCreateBody(name="Gamma"), ctx=ctx_officer, db=db
        )

    async with get_session() as db:
        await notepad.delete_section(
            section_id=section_two.id, ctx=ctx_officer, db=db
        )

    reorder_body = NoteSectionReorderBody(
        sectionIds=[section_three.id, section_one.id]
    )
    async with get_session() as db:
        state = await notepad.reorder_sections(
            body=reorder_body,
            ctx=ctx_officer,
            db=db,
        )

    assert [section.id for section in state.sections] == [
        section_three.id,
        section_one.id,
    ]

    async with get_session() as db:
        result = await db.execute(
            select(NoteSection)
            .where(NoteSection.guild_id == 1)
            .order_by(NoteSection.sort_order, NoteSection.id)
        )
        stored_sections = result.scalars().all()

    active_orders = [s.sort_order for s in stored_sections if not s.is_deleted]
    deleted_orders = [s.sort_order for s in stored_sections if s.is_deleted]

    assert active_orders == [0, 1]
    assert all(order >= len(active_orders) for order in deleted_orders)
