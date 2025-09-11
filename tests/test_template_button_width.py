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

# Stub out discord modules used indirectly by templates routes
discord_mod = types.ModuleType("discord")
ext_mod = types.ModuleType("discord.ext")
commands_mod = types.ModuleType("discord.ext.commands")
sys.modules.setdefault("discord", discord_mod)
sys.modules.setdefault("discord.ext", ext_mod)
sys.modules.setdefault("discord.ext.commands", commands_mod)
commands_mod.Bot = object


class AllowedMentions:
    def __init__(self, **kwargs: object) -> None:
        pass


discord_mod.AllowedMentions = AllowedMentions

from demibot.db.models import Guild, GuildChannel, ChannelKind
from demibot.db.session import init_db, get_session
from demibot.http.schemas import TemplatePayload, EmbedButtonDto
from demibot.http.routes.templates import create_template, TemplateCreateBody


async def _run() -> None:
    db_path = Path("test_template_button_width.db")
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
        buttons=[EmbedButtonDto(label="Join", customId="1", width=10)],
    )
    body = TemplateCreateBody(name="Raid", description="templ", payload=payload)
    ctx = SimpleNamespace(guild=SimpleNamespace(id=1))
    async with get_session() as db:
        dto = await create_template(body=body, ctx=ctx, db=db)
        assert dto.payload.buttons and dto.payload.buttons[0].width == 10


def test_template_button_width() -> None:
    asyncio.run(_run())
