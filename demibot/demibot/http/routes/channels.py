from __future__ import annotations

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse as FastAPIJSONResponse
import json
import logging
import discord
from sqlalchemy import delete, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ...db.models import GuildChannel, ChannelKind
from ...channel_names import ensure_channel_name
from ..discord_client import discord_client
from ..ws import manager

router = APIRouter(prefix="/api")


class JSONResponse(FastAPIJSONResponse):
    def __init__(self, content, *args, ensure_ascii: bool = False, **kwargs):
        self.ensure_ascii = ensure_ascii
        super().__init__(content, *args, **kwargs)

    def render(self, content) -> bytes:
        return json.dumps(
            content,
            ensure_ascii=self.ensure_ascii,
            allow_nan=False,
            indent=None,
            separators=(",", ":"),
        ).encode("utf-8")


@router.get("/channels")
async def get_channels(
    kind: str | None = None,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    async def process_channel(
        channel_id: int, name: str, channel_kind: ChannelKind
    ) -> tuple[dict[str, str] | None, bool]:
        """Return channel payload and whether the DB was updated."""

        changed = False
        channel_obj = None
        if discord_client:
            channel_obj = discord_client.get_channel(channel_id)
            if channel_obj is None:
                try:
                    channel_obj = await discord_client.fetch_channel(channel_id)  # type: ignore[attr-defined]
                except Exception:  # pragma: no cover - best effort
                    channel_obj = None

        if channel_obj is not None:
            if isinstance(
                channel_obj,
                (
                    discord.CategoryChannel,
                    discord.VoiceChannel,
                    discord.StageChannel,
                    discord.ForumChannel,
                ),
            ):
                await db.execute(
                    delete(GuildChannel).where(
                        GuildChannel.guild_id == ctx.guild.id,
                        GuildChannel.channel_id == channel_id,
                        GuildChannel.kind == channel_kind,
                    )
                )
                return None, True
            if isinstance(channel_obj, discord.Thread):
                if getattr(channel_obj, "archived", False):
                    await db.execute(
                        delete(GuildChannel).where(
                            GuildChannel.guild_id == ctx.guild.id,
                            GuildChannel.channel_id == channel_id,
                            GuildChannel.kind == channel_kind,
                        )
                    )
                    return None, True
                parent = getattr(channel_obj, "parent", None)
                parent_id = getattr(parent, "id", None)
                parent_name = getattr(parent, "name", None)
                label = channel_obj.name
                if parent_name:
                    label = f"{parent_name} / {channel_obj.name}"
                if label != name:
                    await db.execute(
                        update(GuildChannel)
                        .where(
                            GuildChannel.guild_id == ctx.guild.id,
                            GuildChannel.channel_id == channel_id,
                            GuildChannel.kind == channel_kind,
                        )
                        .values(name=label)
                    )
                    changed = True
                data: dict[str, str] = {"id": str(channel_id), "name": label}
                if parent_id is not None:
                    data["parentId"] = str(parent_id)
                return data, changed
            # Text channel
            label = channel_obj.name
            if label != name:
                await db.execute(
                    update(GuildChannel)
                    .where(
                        GuildChannel.guild_id == ctx.guild.id,
                        GuildChannel.channel_id == channel_id,
                        GuildChannel.kind == channel_kind,
                    )
                    .values(name=label)
                )
                changed = True
            return {"id": str(channel_id), "name": label}, changed

        # Fallback if the channel couldn't be fetched
        new_name = await ensure_channel_name(
            db, ctx.guild.id, channel_id, channel_kind, name
        )
        if new_name is None:
            logging.warning(
                "Channel name missing for %s (%s) in guild %s",
                channel_id,
                channel_kind.value,
                ctx.guild.id,
            )
            await db.execute(
                update(GuildChannel)
                .where(
                    GuildChannel.guild_id == ctx.guild.id,
                    GuildChannel.channel_id == channel_id,
                    GuildChannel.kind == channel_kind,
                )
                .values(name=None)
            )
            return {"id": str(channel_id), "name": str(channel_id)}, True
        if new_name != name:
            await db.execute(
                update(GuildChannel)
                .where(
                    GuildChannel.guild_id == ctx.guild.id,
                    GuildChannel.channel_id == channel_id,
                    GuildChannel.kind == channel_kind,
                )
                .values(name=new_name)
            )
            changed = True
        return {"id": str(channel_id), "name": new_name}, changed

    # Allow fetching a single channel kind for plugin convenience
    single_kinds = {
        ChannelKind.EVENT.value: ChannelKind.EVENT,
        ChannelKind.FC_CHAT.value: ChannelKind.FC_CHAT,
        ChannelKind.OFFICER_CHAT.value: ChannelKind.OFFICER_CHAT,
    }
    if kind in single_kinds:
        channel_kind = single_kinds[kind]
        result = await db.execute(
            select(GuildChannel.channel_id, GuildChannel.name).where(
                GuildChannel.guild_id == ctx.guild.id,
                GuildChannel.kind == channel_kind,
            )
        )
        channels: list[dict[str, str]] = []
        updated = False
        for channel_id, name in result.all():
            data, changed = await process_channel(channel_id, name, channel_kind)
            if data is not None:
                channels.append(data)
            if changed:
                updated = True
        if updated:
            await db.commit()
            await manager.broadcast_text("update", ctx.guild.id, path="/ws/channels")
        return JSONResponse(content=channels, ensure_ascii=False)

    result = await db.execute(
        select(GuildChannel.kind, GuildChannel.channel_id, GuildChannel.name).where(
            GuildChannel.guild_id == ctx.guild.id
        )
    )
    plugin_kinds = [k for k in ChannelKind if k != ChannelKind.CHAT]
    by_kind: dict[str, list[dict[str, str]]] = {k.value: [] for k in plugin_kinds}
    updated = False
    for chan_kind, channel_id, name in result.all():
        if chan_kind not in plugin_kinds:
            continue
        data, changed = await process_channel(channel_id, name, chan_kind)
        if data is not None:
            by_kind[chan_kind.value].append(data)
        if changed:
            updated = True
    if updated:
        await db.commit()
        await manager.broadcast_text("update", ctx.guild.id, path="/ws/channels")
    return JSONResponse(content=by_kind, ensure_ascii=False)


@router.post("/channels/refresh")
async def refresh_channels(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    result = await db.execute(
        select(GuildChannel.kind, GuildChannel.channel_id, GuildChannel.name).where(
            GuildChannel.guild_id == ctx.guild.id
        )
    )
    plugin_kinds = [k for k in ChannelKind if k != ChannelKind.CHAT]
    updated = False
    for kind, channel_id, name in result.all():
        if kind not in plugin_kinds:
            continue
        new_name = await ensure_channel_name(db, ctx.guild.id, channel_id, kind, name)
        if new_name is None:
            logging.warning(
                "Channel name missing for %s (%s) in guild %s",
                channel_id,
                kind.value,
                ctx.guild.id,
            )
            await db.execute(
                delete(GuildChannel).where(
                    GuildChannel.guild_id == ctx.guild.id,
                    GuildChannel.channel_id == channel_id,
                    GuildChannel.kind == kind,
                )
            )
            updated = True
            continue
        if new_name != name:
            updated = True
    if updated:
        await db.commit()
        await manager.broadcast_text("update", ctx.guild.id, path="/ws/channels")
    return {"status": "ok"}
