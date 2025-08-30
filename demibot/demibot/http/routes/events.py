from __future__ import annotations

import json
from datetime import datetime, timedelta
from typing import List, Optional, Any

import discord
from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import JSONResponse
from sqlalchemy import select, delete
from pydantic import BaseModel
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import EmbedDto, EmbedFieldDto, EmbedButtonDto
from ..ws import manager
from ..discord_client import discord_client
from ...db.models import Embed, EventButton, GuildChannel, RecurringEvent

router = APIRouter(prefix="/api")


class FieldBody(BaseModel):
    name: str
    value: str
    inline: bool | None = None


class CreateEventBody(BaseModel):
    channelId: str
    title: str
    time: str
    description: str
    url: Optional[str] = None
    imageUrl: Optional[str] = None
    thumbnailUrl: Optional[str] = None
    color: Optional[int] = None
    fields: List[FieldBody] | None = None
    buttons: List[EmbedButtonDto] | None = None
    attendance: List[str] | None = None
    mentions: List[str] | None = None
    repeat: Optional[str] = None


@router.post("/events")
async def create_event(
    body: CreateEventBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    eid = str(int(datetime.utcnow().timestamp() * 1000))
    buttons = body.buttons or []
    if not buttons:
        for tag in body.attendance or ["yes", "maybe", "no"]:
            buttons.append(
                EmbedButtonDto(label=tag.capitalize(), customId=f"rsvp:{tag}")
            )
    if body.time:
        time_str = body.time.replace("Z", "+00:00")
        if "." in time_str:
            head, tail = time_str.split(".", 1)
            if "+" in tail:
                frac, tz = tail.split("+", 1)
                time_str = f"{head}.{frac[:6]}+{tz}"
            elif "-" in tail:
                frac, tz = tail.split("-", 1)
                time_str = f"{head}.{frac[:6]}-{tz}"
            else:
                time_str = f"{head}.{tail[:6]}"
        try:
            ts = datetime.fromisoformat(time_str)
        except ValueError:
            return JSONResponse({"error": "Invalid time format"}, status_code=400)
    else:
        ts = datetime.utcnow()

    mention_ids = [int(m) for m in body.mentions or []]

    discord_msg_id: int | None = None
    channel_id = int(body.channelId)
    if discord_client:
        channel = discord_client.get_channel(channel_id)
        if isinstance(channel, discord.abc.Messageable):
            emb = discord.Embed(title=body.title, description=body.description)
            emb.timestamp = ts
            if body.color is not None:
                emb.colour = body.color
            if body.url:
                emb.url = body.url
            for f in body.fields or []:
                emb.add_field(name=f.name, value=f.value, inline=f.inline)
            if body.thumbnailUrl:
                emb.set_thumbnail(url=body.thumbnailUrl)
            if body.imageUrl:
                emb.set_image(url=body.imageUrl)

            view: discord.ui.View | None = None
            if buttons:
                view = discord.ui.View()
                for b in buttons:
                    style = (
                        discord.ButtonStyle(b.style)
                        if b.style is not None
                        else discord.ButtonStyle.secondary
                    )
                    if b.url:
                        view.add_item(
                            discord.ui.Button(
                                label=b.label,
                                url=b.url,
                                emoji=b.emoji,
                                style=style,
                            )
                        )
                    else:
                        view.add_item(
                            discord.ui.Button(
                                label=b.label,
                                custom_id=b.customId,
                                emoji=b.emoji,
                                style=style,
                            )
                        )
            content = " ".join(f"<@&{m}>" for m in mention_ids) or None
            sent = await channel.send(content=content, embed=emb, view=view)
            discord_msg_id = sent.id

    if discord_msg_id is not None:
        eid = str(discord_msg_id)

    dto = EmbedDto(
        id=eid,
        timestamp=ts,
        color=body.color,
        authorName=None,
        authorIconUrl=None,
        title=body.title,
        description=body.description,
        url=body.url,
        fields=[
            EmbedFieldDto(name=f.name, value=f.value, inline=f.inline)
            for f in (body.fields or [])
        ],
        thumbnailUrl=body.thumbnailUrl,
        imageUrl=body.imageUrl,
        buttons=buttons,
        channelId=int(body.channelId) if body.channelId.isdigit() else None,
        mentions=mention_ids or None,
    )
    existing = await db.get(Embed, int(eid))
    if existing:
        existing.channel_id = channel_id
        existing.guild_id = ctx.guild.id
        existing.payload_json = json.dumps(dto.model_dump(mode="json"))
        existing.buttons_json = (
            json.dumps([b.model_dump(mode="json") for b in buttons])
            if buttons
            else None
        )
        await db.execute(
            delete(EventButton).where(EventButton.message_id == int(eid))
        )
        await db.execute(
            delete(RecurringEvent).where(RecurringEvent.id == int(eid))
        )
    else:
        db.add(
            Embed(
                discord_message_id=int(eid),
                channel_id=channel_id,
                guild_id=ctx.guild.id,
                payload_json=json.dumps(dto.model_dump(mode="json")),
                buttons_json=json.dumps([b.model_dump(mode="json") for b in buttons])
                if buttons
                else None,
                source="demibot",
            )
        )
    for b in buttons:
        cid = b.customId
        if cid and cid.startswith("rsvp:"):
            tag = cid.split(":", 1)[1]
            db.add(
                EventButton(
                    message_id=int(eid),
                    tag=tag,
                    label=b.label,
                    emoji=b.emoji,
                    style=int(b.style) if b.style is not None else None,
                    max_signups=b.maxSignups,
                )
            )
    if body.repeat in ("daily", "weekly"):
        interval = timedelta(days=1 if body.repeat == "daily" else 7)
        next_post = ts + interval
        payload = body.model_dump()
        payload["repeat"] = None
        db.add(
            RecurringEvent(
                id=int(eid),
                guild_id=ctx.guild.id,
                channel_id=channel_id,
                repeat=body.repeat,
                next_post_at=next_post,
                payload_json=json.dumps(payload),
            )
        )
    await db.commit()
    kind = (
        await db.execute(
            select(GuildChannel.kind).where(
                GuildChannel.guild_id == ctx.guild.id,
                GuildChannel.channel_id == channel_id,
                GuildChannel.kind == "officer_chat",
            )
        )
    ).scalar_one_or_none()
    await manager.broadcast_text(
        json.dumps(dto.model_dump(mode="json")),
        ctx.guild.id,
        officer_only=kind == "officer_chat",
        path="/ws/embeds",
    )
    return {"ok": True, "id": eid}


class RepeatPatchBody(BaseModel):
    repeat: Optional[str] = None
    time: Optional[str] = None


@router.get("/events/repeat")
async def list_recurring_events(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> List[dict[str, Any]]:
    result = await db.execute(
        select(RecurringEvent).where(RecurringEvent.guild_id == ctx.guild.id)
    )
    rows: List[dict[str, Any]] = []
    for ev in result.scalars():
        try:
            payload = json.loads(ev.payload_json)
            title = payload.get("title")
        except Exception:
            title = None
        rows.append(
            {
                "id": str(ev.id),
                "channelId": str(ev.channel_id),
                "repeat": ev.repeat,
                "next": ev.next_post_at.strftime("%Y-%m-%dT%H:%M:%S.%fZ"),
                "title": title,
            }
        )
    return rows


@router.patch("/events/{event_id}/repeat", response_model=None)
async def update_recurring_event(
    event_id: str,
    body: RepeatPatchBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, bool]:
    rid = int(event_id)
    ev = await db.get(RecurringEvent, rid)
    if not ev or ev.guild_id != ctx.guild.id:
        raise HTTPException(status_code=404)
    if body.repeat in ("daily", "weekly"):
        ev.repeat = body.repeat
    if body.time:
        time_str = body.time.replace("Z", "+00:00")
        if "." in time_str:
            head, tail = time_str.split(".", 1)
            if "+" in tail:
                frac, tz = tail.split("+", 1)
                time_str = f"{head}.{frac[:6]}+{tz}"
            elif "-" in tail:
                frac, tz = tail.split("-", 1)
                time_str = f"{head}.{frac[:6]}-{tz}"
            else:
                time_str = f"{head}.{tail[:6]}"
        try:
            ts = datetime.fromisoformat(time_str)
        except ValueError:
            raise HTTPException(status_code=400, detail="Invalid time format")
        ev.next_post_at = ts
    await db.commit()
    return {"ok": True}


@router.delete("/events/{event_id}/repeat")
async def delete_recurring_event(
    event_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, bool]:
    rid = int(event_id)
    ev = await db.get(RecurringEvent, rid)
    if ev and ev.guild_id == ctx.guild.id:
        await db.delete(ev)
        await db.commit()
    return {"ok": True}
