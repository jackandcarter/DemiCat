import json
import sys
import types
from pathlib import Path

# Setup module paths identical to other websocket sync tests
root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

# Package stubs for namespace packages when running without installation
demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)
http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

deps_pkg = types.ModuleType("demibot.http.deps")


class RequestContext:
    def __init__(self, guild, user, roles):
        self.guild = guild
        self.user = user
        self.roles = roles


def api_key_auth(*args, **kwargs):  # pragma: no cover - unused but kept for parity
    pass

async def get_db(*args, **kwargs):
    if False:
        yield None


deps_pkg.RequestContext = RequestContext
deps_pkg.api_key_auth = api_key_auth
deps_pkg.get_db = get_db
sys.modules.setdefault("demibot.http.deps", deps_pkg)

# Minimal discord stub for modules that expect discord.py
sys.modules.setdefault("discord", types.ModuleType("discord"))

import pytest

from demibot.http.ws import ConnectionManager
from demibot.http.routes import notepad
from demibot.db.models import Guild, User
from demibot.db.session import get_session, init_db
from demibot.http.schemas import (
    NotePageContentBody,
    NotePageCreateBody,
    NoteSectionCreateBody,
)


class StubWebSocket:
    def __init__(self, path: str):
        self.scope = {"path": path}
        self.sent: list[str] = []

    async def accept(self) -> None:
        pass

    async def send_text(self, message: str) -> None:
        self.sent.append(message)

    async def ping(self) -> None:  # pragma: no cover - compatibility shim
        return None


class StubContext:
    def __init__(self, guild_id: int, user_id: int, roles: list[str]):
        self.guild = types.SimpleNamespace(id=guild_id)
        self.user = types.SimpleNamespace(id=user_id)
        self.roles = roles


@pytest.fixture
def anyio_backend():
    return "asyncio"


@pytest.mark.anyio
async def test_notepad_sync_and_conflict_flow(tmp_path, monkeypatch):
    db_path = Path(tmp_path) / "notepad_sync.db"
    await init_db(f"sqlite+aiosqlite:///{db_path}")

    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Guild")
        user = User(id=1, discord_user_id=42, global_name="Officer")
        db.add_all([guild, user])
        await db.commit()

    manager = ConnectionManager()
    monkeypatch.setattr(notepad, "manager", manager)

    ws_member = StubWebSocket("/ws/notepad")
    ws_officer = StubWebSocket("/ws/notepad")
    await manager.connect(ws_member, StubContext(1, 2, roles=[]))
    await manager.connect(ws_officer, StubContext(1, 1, roles=["officer"]))

    ctx_member = StubContext(1, 2, roles=[])
    ctx_officer = StubContext(1, 1, roles=["officer"])

    async with get_session() as db:
        section = await notepad.create_section(
            body=NoteSectionCreateBody(name="Raid", color=0x123456),
            ctx=ctx_officer,
            db=db,
        )

    assert len(ws_member.sent) == 1
    assert json.loads(ws_member.sent[-1])["topic"] == "notepad.section.created"
    assert ws_member.sent[-1] == ws_officer.sent[-1]

    async with get_session() as db:
        page = await notepad.create_page(
            body=NotePageCreateBody(sectionId=section.id, title="Notes", content="Initial"),
            ctx=ctx_officer,
            db=db,
        )

    assert len(ws_member.sent) == 2
    assert json.loads(ws_member.sent[-1])["topic"] == "notepad.page.created"

    async with get_session() as db:
        updated = await notepad.update_page_content(
            page_id=page.id,
            body=NotePageContentBody(content="Revised", version=page.version),
            ctx=ctx_officer,
            db=db,
        )

    assert len(ws_officer.sent) == 3
    assert json.loads(ws_officer.sent[-1])["topic"] == "notepad.page.updated"

    stale_version = page.version
    async with get_session() as db:
        with pytest.raises(Exception) as excinfo:
            await notepad.update_page_content(
                page_id=page.id,
                body=NotePageContentBody(content="Stale", version=stale_version),
                ctx=ctx_officer,
                db=db,
            )
    assert getattr(excinfo.value, "status_code", None) == 409

    # Conflict should not broadcast a new websocket payload
    assert len(ws_member.sent) == 3
    assert ws_member.sent == ws_officer.sent

    # Members can still read updated state
    async with get_session() as db:
        state = await notepad.list_notepad(ctx=ctx_member, db=db)
    assert state.sections[0].pages[0].content == "Revised"
    assert state.sections[0].pages[0].version == updated.version
