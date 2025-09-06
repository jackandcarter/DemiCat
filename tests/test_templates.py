import sys
from pathlib import Path
import types
import asyncio
from types import SimpleNamespace

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

discord_mod = types.ModuleType("discord")
ext_mod = types.ModuleType("discord.ext")
commands_mod = types.ModuleType("discord.ext.commands")
sys.modules.setdefault("discord", discord_mod)
sys.modules.setdefault("discord.ext", ext_mod)
sys.modules.setdefault("discord.ext.commands", commands_mod)
commands_mod.Bot = object

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
    payload = TemplatePayload(
        channelId="123",
        title="Test Event",
        time="2024-01-01T00:00:00Z",
        description="desc",
    )
    body = TemplateCreateBody(name="Raid", description="templ", payload=payload)
    ctx = SimpleNamespace(guild=SimpleNamespace(id=1))
    async with get_session() as db:
        dto = await create_template(body=body, ctx=ctx, db=db)
        tid = dto.id
        lst = await list_templates(ctx=ctx, db=db)
        assert len(lst) == 1 and lst[0].id == tid
        g = await get_template(template_id=tid, ctx=ctx, db=db)
        assert g.name == "Raid"
        upd = TemplateUpdateBody(name="Raid2")
        dto2 = await update_template(template_id=tid, body=upd, ctx=ctx, db=db)
        assert dto2.name == "Raid2"
        res = await post_template(template_id=tid, ctx=ctx, db=db)
        assert "id" in res
        embed = await db.get(Embed, int(res["id"]))
        assert embed is not None
        await delete_template(template_id=tid, ctx=ctx, db=db)
        lst = await list_templates(ctx=ctx, db=db)
        assert lst == []


def test_templates_crud_and_post() -> None:
    asyncio.run(_run_test())
