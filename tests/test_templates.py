import sys
from pathlib import Path
import types
import asyncio
from types import SimpleNamespace
import json

import pytest

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)
routes_pkg = types.ModuleType("demibot.http.routes")
routes_pkg.__path__ = [str(root / "demibot/http/routes")]
sys.modules.setdefault("demibot.http.routes", routes_pkg)
structlog_stub = types.ModuleType("structlog")
structlog_stub.get_logger = lambda *a, **k: None
sys.modules.setdefault("structlog", structlog_stub)

discord_mod = types.ModuleType("discord")
ext_mod = types.ModuleType("discord.ext")
commands_mod = types.ModuleType("discord.ext.commands")
sys.modules.setdefault("discord", discord_mod)
sys.modules.setdefault("discord.ext", ext_mod)
sys.modules.setdefault("discord.ext.commands", commands_mod)
commands_mod.Bot = object
discord_mod.ClientException = type("ClientException", (Exception,), {})
discord_mod.HTTPException = type("DiscordHTTPException", (Exception,), {})
discord_mod.File = type("File", (), {})
discord_mod.Thread = type("Thread", (), {})
discord_mod.Webhook = type("Webhook", (), {})
discord_mod.ForumChannel = type("ForumChannel", (), {})
discord_mod.CategoryChannel = type("CategoryChannel", (), {})
discord_mod.TextChannel = type("TextChannel", (), {})
discord_mod.Message = type("Message", (), {})
discord_mod.Forbidden = type("Forbidden", (Exception,), {})
class _Embed:
    def __init__(self, *args, **kwargs):
        self.timestamp = None
        self.colour = None
        self.url = None

    def add_field(self, *args, **kwargs):
        return None

    def set_thumbnail(self, *args, **kwargs):
        return None

    def set_image(self, *args, **kwargs):
        return None

discord_mod.Embed = _Embed
class _ButtonStyle:
    secondary = "secondary"

    def __call__(self, value):
        return value

discord_mod.ButtonStyle = _ButtonStyle()
class _PartialEmoji:
    def __init__(self, *args, **kwargs):
        pass

discord_mod.PartialEmoji = _PartialEmoji
class _AllowedMentions:
    def __init__(self, *args, **kwargs):
        pass

discord_mod.AllowedMentions = _AllowedMentions
class _DiscordObject:
    def __init__(self, *, id):
        self.id = id

discord_mod.Object = _DiscordObject
discord_http_mod = types.ModuleType("discord.http")
discord_http_mod.handle_message_parameters = lambda *args, **kwargs: None
discord_utils_mod = types.ModuleType("discord.utils")
discord_utils_mod.MISSING = object()
discord_abc_mod = types.ModuleType("discord.abc")
discord_abc_mod.Messageable = type("Messageable", (), {})
sys.modules.setdefault("discord.http", discord_http_mod)
sys.modules.setdefault("discord.utils", discord_utils_mod)
sys.modules.setdefault("discord.abc", discord_abc_mod)
discord_mod.abc = discord_abc_mod
discord_ui_mod = types.ModuleType("discord.ui")
class _View:
    def __init__(self, *args, **kwargs):
        self.items: list[object] = []

    def add_item(self, item):
        self.items.append(item)

class _Button:
    def __init__(self, *args, **kwargs):
        pass

discord_ui_mod.View = _View
discord_ui_mod.Button = _Button
sys.modules.setdefault("discord.ui", discord_ui_mod)
discord_mod.ui = discord_ui_mod

from demibot.db.models import Guild, GuildChannel, ChannelKind, Embed
from demibot.db.session import init_db, get_session
from demibot.http.schemas import TemplatePayload
from demibot.http.routes.templates import (
    create_template,
    list_templates,
    get_template,
    update_template,
    delete_template,
    post_template,
    TemplateCreateBody,
    TemplateUpdateBody,
    HTTPException,
)


async def _run_test() -> None:
    db_path = Path("test_templates.db")
    if db_path.exists():
        db_path.unlink()
    url = f"sqlite+aiosqlite:///{db_path}"
    await init_db(url)
    async with get_session() as db:
        guild = Guild(id=1, discord_guild_id=1, name="Test Guild")
        db.add(guild)
        db.add(GuildChannel(guild_id=guild.id, channel_id=123, kind=ChannelKind.EVENT))
        await db.commit()
    invalid_payload = TemplatePayload(
        channelId="123",
        title="Invalid Event",
        time="not-a-time",
        description="desc",
    )
    invalid_body = TemplateCreateBody(
        name="Bad", description="templ", payload=invalid_payload
    )
    payload = TemplatePayload(
        channelId="123",
        title="Test Event",
        time="2024-01-01T00:00:00Z",
        description="desc",
    )
    body = TemplateCreateBody(name="Raid", description="templ", payload=payload)
    ctx = SimpleNamespace(guild=SimpleNamespace(id=1), roles=[])
    async with get_session() as db:
        with pytest.raises(HTTPException) as excinfo:
            await create_template(body=invalid_body, ctx=ctx, db=db)
        assert excinfo.value.status_code == 400
    async with get_session() as db:
        dto = await create_template(body=body, ctx=ctx, db=db)
        dup = await create_template(body=body, ctx=ctx, db=db)
        assert dup.status_code == 409
        assert json.loads(dup.body.decode()) == {"error": "duplicate"}
        tid = dto.id
        lst = await list_templates(ctx=ctx, db=db)
        assert len(lst) == 1 and lst[0].id == tid
        g = await get_template(template_id=tid, ctx=ctx, db=db)
        assert g.name == "Raid"
        upd = TemplateUpdateBody(name="Raid2")
        dto2 = await update_template(template_id=tid, body=upd, ctx=ctx, db=db)
        assert dto2.name == "Raid2"
        bad_update_payload = TemplatePayload(
            channelId="123",
            title="Test Event",
            time="bad-time",
            description="desc",
        )
        with pytest.raises(HTTPException) as excinfo_update:
            await update_template(
                template_id=tid,
                body=TemplateUpdateBody(payload=bad_update_payload),
                ctx=ctx,
                db=db,
            )
        assert excinfo_update.value.status_code == 400
        res = await post_template(template_id=tid, ctx=ctx, db=db)
        assert "id" in res
        embed = await db.get(Embed, int(res["id"]))
        assert embed is not None
        await delete_template(template_id=tid, ctx=ctx, db=db)
        lst = await list_templates(ctx=ctx, db=db)
        assert lst == []


def test_templates_crud_and_post() -> None:
    asyncio.run(_run_test())
