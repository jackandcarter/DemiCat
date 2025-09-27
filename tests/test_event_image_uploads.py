import sys
from pathlib import Path
import types
import asyncio
import json
import importlib.util
from types import SimpleNamespace
from unittest.mock import patch


if "discord" not in sys.modules:
    discord_pkg = types.ModuleType("discord")

    class _HTTPException(Exception):
        def __init__(self, response=None, message: str = "") -> None:
            super().__init__(message)
            self.status = getattr(response, "status", 0)
            self.text = message
            self.retry_after = getattr(response, "retry_after", 0)

    class _Embed:
        def __init__(self, *, title: str | None = None, description: str | None = None) -> None:
            self.title = title
            self.description = description
            self.fields: list[dict[str, object]] = []
            self.image = SimpleNamespace(url=None)
            self.thumbnail = SimpleNamespace(url=None)
            self.footer = SimpleNamespace(text=None)
            self.timestamp = None
            self.colour = None
            self.url = None
            self.author = SimpleNamespace(name=None, icon_url=None)

        def add_field(self, *, name: str, value: str, inline: bool | None = None) -> None:
            self.fields.append({"name": name, "value": value, "inline": inline})

        def set_thumbnail(self, *, url: str | None = None) -> None:
            self.thumbnail.url = url

        def set_image(self, *, url: str | None = None) -> None:
            self.image.url = url

        def set_footer(self, *, text: str | None = None) -> None:
            self.footer.text = text

        def set_author(self, *, name: str | None = None, icon_url: str | None = None) -> None:
            self.author.name = name
            self.author.icon_url = icon_url

        def to_dict(self) -> dict[str, object]:
            data: dict[str, object] = {
                "title": self.title,
                "description": self.description,
                "fields": self.fields,
            }
            if self.image.url:
                data["image"] = {"url": self.image.url}
            if self.thumbnail.url:
                data["thumbnail"] = {"url": self.thumbnail.url}
            if self.footer.text:
                data["footer"] = {"text": self.footer.text}
            if self.url:
                data["url"] = self.url
            return data

    class _ButtonStyle(int):
        primary = 1
        secondary = 2
        success = 3
        danger = 4
        link = 5

    class _PartialEmoji:
        def __init__(self, *, name: str | None = None, id: int | None = None, animated: bool = False) -> None:
            self.name = name
            self.id = id
            self.animated = animated

    class _File:
        def __init__(self, fp, filename: str) -> None:
            self.fp = fp
            self.filename = filename
            self.content_type: str | None = None

        def reset(self) -> None:
            try:
                self.fp.seek(0)
            except Exception:  # pragma: no cover - defensive
                pass

    class _Messageable:
        async def _get_channel(self):  # pragma: no cover - compatibility stub
            return self

    abc_module = types.ModuleType("discord.abc")
    abc_module.Messageable = _Messageable

    ui_module = types.ModuleType("discord.ui")

    class _View:
        def __init__(self) -> None:
            self.items: list[object] = []

        def add_item(self, item: object) -> None:
            self.items.append(item)

    class _Button:
        def __init__(self, *, label: str | None = None, url: str | None = None, emoji: object = None, style: _ButtonStyle | None = None, row: int | None = None, custom_id: str | None = None) -> None:
            self.label = label
            self.url = url
            self.emoji = emoji
            self.style = style
            self.row = row
            self.custom_id = custom_id

    ui_module.View = _View
    ui_module.Button = _Button

    discord_pkg.HTTPException = _HTTPException
    discord_pkg.Embed = _Embed
    discord_pkg.PartialEmoji = _PartialEmoji
    discord_pkg.File = _File
    discord_pkg.ButtonStyle = _ButtonStyle
    discord_pkg.ui = ui_module
    discord_pkg.abc = abc_module
    discord_pkg.Thread = type("Thread", (_Messageable,), {})
    discord_pkg.TextChannel = type("TextChannel", (_Messageable,), {})
    discord_pkg.CategoryChannel = type("CategoryChannel", (_Messageable,), {})
    discord_pkg.ClientException = type("ClientException", (Exception,), {})

    ext_module = types.ModuleType("discord.ext")
    commands_module = types.ModuleType("discord.ext.commands")

    class _DummyBot:
        pass

    commands_module.Bot = _DummyBot
    ext_module.commands = commands_module

    discord_pkg.ext = ext_module
    sys.modules["discord"] = discord_pkg
    sys.modules["discord.ui"] = ui_module
    sys.modules["discord.abc"] = abc_module
    sys.modules["discord.ext"] = ext_module
    sys.modules["discord.ext.commands"] = commands_module

import discord

sys.modules.setdefault("aiohttp", types.ModuleType("aiohttp"))

root = Path(__file__).resolve().parents[1] / "demibot"
sys.path.append(str(root))

demibot_pkg = types.ModuleType("demibot")
demibot_pkg.__path__ = [str(root / "demibot")]
sys.modules.setdefault("demibot", demibot_pkg)

http_pkg = types.ModuleType("demibot.http")
http_pkg.__path__ = [str(root / "demibot/http")]
sys.modules.setdefault("demibot.http", http_pkg)

models_pkg = types.ModuleType("demibot.models")
models_pkg.__path__ = [str(root / "demibot/models")]
sys.modules.setdefault("demibot.models", models_pkg)

from sqlalchemy import select

from demibot.db.models import Guild, GuildChannel, ChannelKind  # noqa: E402
from demibot.db.session import init_db, get_session  # noqa: E402
from demibot.http.event_images import event_image_library  # noqa: E402

events_spec = importlib.util.spec_from_file_location(
    "demibot.http.routes.events", root / "demibot/http/routes/events.py"
)
events_route = importlib.util.module_from_spec(events_spec)
sys.modules["demibot.http.routes.events"] = events_route
assert events_spec.loader is not None
events_spec.loader.exec_module(events_route)

create_event = events_route.create_event
CreateEventBody = events_route.CreateEventBody
Event = events_route.Event


class DummyAttachment:
    def __init__(self, url: str, filename: str, content_type: str | None, size: int | None) -> None:
        self.url = url
        self.filename = filename
        self.content_type = content_type
        self.size = size


class DummyTextChannel(discord.abc.Messageable):
    def __init__(self) -> None:
        self.id = 555
        self.captured_filenames: list[str] | None = None
        self.embed_url: str | None = None
        self.thumbnail_url: str | None = None

    async def _get_channel(self):  # pragma: no cover - required by Messageable
        return self

    async def send(self, *args, **kwargs):
        files = kwargs.get("files")
        self.captured_filenames = [f.filename for f in files] if files else []
        embed = kwargs.get("embed")
        if embed is not None:
            self.embed_url = getattr(getattr(embed, "image", None), "url", None)
            self.thumbnail_url = getattr(getattr(embed, "thumbnail", None), "url", None)
        message_embed = discord.Embed(title="Posted")
        message_embed.set_image(url="https://cdn.example/banner.png")
        message_embed.set_thumbnail(url="https://cdn.example/thumb.png")
        attachments = [
            DummyAttachment("https://cdn.example/banner.png", "banner.png", "image/png", 4),
            DummyAttachment("https://cdn.example/thumb.png", "thumb.png", "image/png", 4),
        ]
        return SimpleNamespace(id=987, embeds=[message_embed], attachments=attachments)


async def _run_test() -> None:
    await init_db("sqlite+aiosqlite://")
    async with get_session() as db:
        await event_image_library.clear()
        db.add(Guild(id=1, discord_guild_id=1, name="Guild"))
        db.add(GuildChannel(guild_id=1, channel_id=777, kind=ChannelKind.EVENT))
        await db.commit()

    await event_image_library.store_bytes(
        "banner1",
        data=b"banner",
        filename="banner.png",
        content_type="image/png",
    )
    await event_image_library.store_bytes(
        "thumb1",
        data=b"thumb",
        filename="thumb.png",
        content_type="image/png",
    )

    body = CreateEventBody(
        channelId="777",
        title="Test",
        description="Desc",
        imageId="banner1",
        thumbnailId="thumb1",
    )
    ctx = SimpleNamespace(guild=SimpleNamespace(id=1), user=SimpleNamespace(id=42, global_name=None), roles=[])
    dummy_channel = DummyTextChannel()

    async def dummy_broadcast(*args, **kwargs):
        return None

    async def dummy_emit(*args, **kwargs):
        return None

    client = SimpleNamespace(get_channel=lambda cid: dummy_channel)
    original_dumps = json.dumps

    async with get_session() as db:
        with patch("demibot.http.routes.events.discord_client", client), patch(
            "demibot.http.routes.events.manager.broadcast_text", dummy_broadcast
        ), patch("demibot.http.routes.events.emit_event", dummy_emit), patch(
            "demibot.http.routes.events.json.dumps",
            lambda obj, *a, **k: original_dumps(obj, default=str, *a, **k),
        ):
            result = await create_event(body=body, ctx=ctx, db=db)

        event_row = (await db.execute(select(Event))).scalar_one()

    # Ensure files were attached with the expected filenames and embed referenced attachments
    assert dummy_channel.captured_filenames == ["banner.png", "thumb.png"]
    assert dummy_channel.embed_url == "attachment://banner.png"
    assert dummy_channel.thumbnail_url == "attachment://thumb.png"

    # API response should expose attachment metadata from Discord
    assert result["attachments"] == [
        {"url": "https://cdn.example/banner.png", "filename": "banner.png", "contentType": "image/png", "size": 4},
        {"url": "https://cdn.example/thumb.png", "filename": "thumb.png", "contentType": "image/png", "size": 4},
    ]

    # Persisted data should reference the CDN URLs
    assert event_row.embeds[0]["image"]["url"] == "https://cdn.example/banner.png"
    assert event_row.embeds[0]["thumbnail"]["url"] == "https://cdn.example/thumb.png"
    assert event_row.attachments == result["attachments"]

    await event_image_library.clear()


def test_event_image_attachments() -> None:
    asyncio.run(_run_test())
