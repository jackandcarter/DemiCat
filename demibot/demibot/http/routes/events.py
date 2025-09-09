from __future__ import annotations

import json
import logging
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional

import discord
from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field, field_validator
from sqlalchemy import delete, func, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import EmbedDto, EmbedFieldDto, EmbedButtonDto, AttachmentDto, ButtonStyle
from ..validation import validate_embed_payload
from ..ws import manager
from ..chat_events import emit_event
from ..discord_client import discord_client
from ...discordbot.utils import api_call_with_retries
from ...db.models import (
    Embed,
    EventButton,
    GuildChannel,
    RecurringEvent,
    ChannelKind,
    EventSignup,
    User,
)
from models.event import Event

router = APIRouter(prefix="/api")


def summarize(att: Dict[str, List[str]], labels: Dict[str, str], order: List[str]) -> List[dict]:
    fields = []
    for key in order:
        people = att.get(key, [])
        label = labels.get(key, key.capitalize())
        fields.append({"name": label, "value": ", ".join(people) if people else "—"})
    return fields


class FieldBody(BaseModel):
    name: str
    value: str
    inline: bool | None = None


class CreateEventBody(BaseModel):
    channel_id: str = Field(alias="channelId")
    title: str
    time: datetime | None = None
    description: str
    url: Optional[str] = None
    image_url: Optional[str] = Field(default=None, alias="imageUrl")
    thumbnail_url: Optional[str] = Field(default=None, alias="thumbnailUrl")
    color: Optional[int] = None
    fields: List[FieldBody] | None = None
    buttons: List[EmbedButtonDto] | None = None
    attendance: List[str] | None = None
    mentions: List[str] | None = None
    repeat: Optional[str] = None
    embeds: List[dict] | None = None
    attachments: List[AttachmentDto] | None = None

    @field_validator("time", mode="before")
    @classmethod
    def _parse_time(cls, value: Any) -> Any:
        if value is None:
            return None
        if isinstance(value, datetime):
            dt = value
        else:
            s = value.replace("Z", "+00:00")
            if "." in s:
                head, tail = s.split(".", 1)
                if "+" in tail:
                    frac, tz = tail.split("+", 1)
                    s = f"{head}.{frac[:6]}+{tz}"
                elif "-" in tail:
                    frac, tz = tail.split("-", 1)
                    s = f"{head}.{frac[:6]}-{tz}"
                else:
                    s = f"{head}.{tail[:6]}"
            try:
                dt = datetime.fromisoformat(s)
            except ValueError:
                raise HTTPException(status_code=400, detail="Invalid time format")
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        else:
            dt = dt.astimezone(timezone.utc)
        return dt


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
                EmbedButtonDto(label=tag.capitalize(), custom_id=f"rsvp:{tag}")
            )
    if body.time:
        ts = body.time
    else:
        ts = datetime.now(timezone.utc)

    channel_id = int(body.channel_id)
    mention_ids = [int(m) for m in body.mentions or []]

    dto = EmbedDto(
        id=eid,
        timestamp=ts,
        color=body.color,
        author_name=None,
        author_icon_url=None,
        title=body.title,
        description=body.description,
        url=body.url,
        fields=[
            EmbedFieldDto(name=f.name, value=f.value, inline=f.inline)
            for f in (body.fields or [])
        ],
        thumbnail_url=body.thumbnail_url,
        image_url=body.image_url,
        buttons=buttons,
        channel_id=channel_id,
        mentions=mention_ids or None,
    )
    validate_embed_payload(dto, buttons)

    stored_embeds = body.embeds
    stored_attachments = (
        [a.model_dump(mode="json", by_alias=True, exclude_none=True) for a in body.attachments]
        if body.attachments
        else None
    )

    discord_msg_id: int | None = None
    sent: discord.Message | None = None
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
            if body.thumbnail_url:
                emb.set_thumbnail(url=body.thumbnail_url)
            if body.image_url:
                emb.set_image(url=body.image_url)

            view: discord.ui.View | None = None
            if buttons:
                if len(buttons) > 25:
                    raise HTTPException(422, "Too many buttons (max 25)")
                view = discord.ui.View()
                rows: Dict[int, List[EmbedButtonDto]] = {}
                for b in buttons:
                    row = b.row_index or 0
                    rows.setdefault(row, []).append(b)
                if len(rows) > 5 or any(r >= 5 for r in rows):
                    raise HTTPException(422, "Too many button rows (max 5)")
                for row_index in sorted(rows):
                    row_buttons = rows[row_index]
                    if len(row_buttons) > 5:
                        raise HTTPException(422, "Too many buttons in row (max 5)")
                    for b in row_buttons:
                        if b.style == ButtonStyle.link:
                            if not b.url:
                                raise HTTPException(422, "Link buttons require a URL")
                            b.custom_id = None
                        else:
                            if not b.custom_id:
                                raise HTTPException(422, "Non-link buttons require customId")
                            b.url = None
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
                                    row=row_index,
                                )
                            )
                        else:
                            view.add_item(
                                discord.ui.Button(
                                    label=b.label,
                                    custom_id=b.custom_id,
                                    emoji=b.emoji,
                                    style=style,
                                    row=row_index,
                                )
                            )
            content = " ".join(f"<@&{m}>" for m in mention_ids) or None
            try:
                sent = await api_call_with_retries(
                    channel.send, content=content, embed=emb, view=view
                )
            except discord.HTTPException as exc:
                if 500 <= getattr(exc, "status", 0) < 600:
                    logging.error(
                        "Discord API error",
                        extra={"status": exc.status, "error": exc.text},
                    )
                    return JSONResponse(
                        {
                            "error": "discord_api_error",
                            "status": exc.status,
                            "message": exc.text,
                        },
                        status_code=502,
                    )
                raise
            discord_msg_id = sent.id
            if stored_embeds is None and sent.embeds:
                stored_embeds = [e.to_dict() for e in sent.embeds]
            if stored_attachments is None and sent.attachments:
                stored_attachments = [
                    {
                        "url": a.url,
                        "filename": a.filename,
                        "contentType": a.content_type,
                    }
                    for a in sent.attachments
                ]

    if discord_msg_id is not None:
        eid = str(discord_msg_id)
        dto.id = eid
    existing = await db.get(Embed, int(eid))
    if existing:
        existing.channel_id = channel_id
        existing.guild_id = ctx.guild.id
        existing.payload_json = json.dumps(
            dto.model_dump(mode="json", by_alias=True, exclude_none=True)
        )
        existing.buttons_json = (
            json.dumps(
                [b.model_dump(mode="json", by_alias=True, exclude_none=True) for b in buttons]
            )
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
                payload_json=json.dumps(
                    dto.model_dump(mode="json", by_alias=True, exclude_none=True)
                ),
                buttons_json=json.dumps(
                    [
                        b.model_dump(mode="json", by_alias=True, exclude_none=True)
                        for b in buttons
                    ]
                )
                if buttons
                else None,
                source="demibot",
            )
        )
        db.add(
            Event(
                discord_message_id=int(eid),
                channel_id=channel_id,
                guild_id=ctx.guild.id,
                embeds=stored_embeds,
                attachments=stored_attachments,
            )
        )

    for b in buttons:
        cid = b.custom_id
        if cid and cid.startswith("rsvp:"):
            tag = cid.split(":", 1)[1]
            db.add(
                EventButton(
                    message_id=int(eid),
                    tag=tag,
                    label=b.label,
                    emoji=b.emoji,
                    style=int(b.style) if b.style is not None else None,
                    max_signups=b.max_signups,
                )
            )
    if body.repeat in ("daily", "weekly"):
        interval = timedelta(days=1 if body.repeat == "daily" else 7)
        next_post = ts + interval
        payload = body.model_dump(mode="json", by_alias=True, exclude_none=True)
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
                GuildChannel.kind == ChannelKind.OFFICER_CHAT,
            )
        )
    ).scalar_one_or_none()
    payload = dto.model_dump(mode="json", by_alias=True, exclude_none=True)
    await manager.broadcast_text(
        json.dumps(payload),
        ctx.guild.id,
        officer_only=kind == ChannelKind.OFFICER_CHAT,
        path="/ws/embeds",
    )
    await emit_event({"channel": channel_id, "op": "ec", "d": payload})
    return {"ok": True, "id": eid}


@router.get("/events")
async def list_events(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> List[dict[str, Any]]:
    result = await db.execute(
        select(Event).where(Event.guild_id == ctx.guild.id)
    )
    rows: List[dict[str, Any]] = []
    for ev in result.scalars():
        rows.append(
            {
                "id": str(ev.discord_message_id),
                "channelId": str(ev.channel_id),
                "embeds": ev.embeds,
                "attachments": ev.attachments,
            }
        )
    return rows


class RsvpBody(BaseModel):
    tag: str


@router.post("/events/{event_id}/rsvp")
async def rsvp_event(
    event_id: str,
    body: RsvpBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    message_id = int(event_id)
    tag = body.tag
    embed = (await db.execute(select(Embed).where(Embed.discord_message_id == message_id))).scalar_one_or_none()
    labels: Dict[str, str] = {}
    order: List[str] = []
    limits: Dict[str, int] = {}

    rows = await db.execute(select(EventButton).where(EventButton.message_id == message_id))
    for b in rows.scalars():
        order.append(b.tag)
        labels[b.tag] = b.label
        if b.max_signups is not None:
            limits[b.tag] = b.max_signups

    if embed:
        payload = json.loads(embed.payload_json)

    stmt = select(EventSignup).where(
        EventSignup.discord_message_id == message_id,
        EventSignup.user_id == ctx.user.id,
    )
    row = (await db.execute(stmt)).scalar_one_or_none()
    if row and row.choice == tag:
        await db.delete(row)
    else:
        if row and row.choice != tag:
            limit = limits.get(tag)
            if limit is not None:
                count_stmt = select(func.count()).where(
                    EventSignup.discord_message_id == message_id,
                    EventSignup.choice == tag,
                )
                count = (await db.execute(count_stmt)).scalar_one()
                if count >= limit:
                    return JSONResponse({"error": "Full"}, status_code=400)
            row.choice = tag
            row.created_at = datetime.utcnow()
        elif row is None:
            limit = limits.get(tag)
            if limit is not None:
                count_stmt = select(func.count()).where(
                    EventSignup.discord_message_id == message_id,
                    EventSignup.choice == tag,
                )
                count = (await db.execute(count_stmt)).scalar_one()
                if count >= limit:
                    return JSONResponse({"error": "Full"}, status_code=400)
            db.add(
                EventSignup(
                    discord_message_id=message_id,
                    user_id=ctx.user.id,
                    choice=tag,
                    created_at=datetime.utcnow(),
                )
            )
    await db.commit()

    stmt = (
        select(
            EventSignup.choice,
            User.global_name,
            User.discriminator,
            User.discord_user_id,
        )
        .join(User, EventSignup.user_id == User.id)
        .where(EventSignup.discord_message_id == message_id)
    )
    rows = await db.execute(stmt)
    summary: Dict[str, List[str]] = {}
    for c, name, discrim, uid in rows.all():
        display = name or discrim or str(uid)
        summary.setdefault(c, []).append(display)

    if embed:
        payload["fields"] = summarize(summary, labels, order or list(summary.keys()))
        embed.payload_json = json.dumps(payload)
        await db.commit()
        kind = (
            await db.execute(
                select(GuildChannel.kind).where(
                    GuildChannel.guild_id == embed.guild_id,
                    GuildChannel.channel_id == embed.channel_id,
                )
            )
        ).scalar_one_or_none()
        await manager.broadcast_text(
            json.dumps(payload),
            embed.guild_id,
            officer_only=kind == ChannelKind.OFFICER_CHAT,
            path="/ws/embeds",
        )
        await emit_event({"channel": embed.channel_id, "op": "eu", "d": payload})

    return {"ok": True}


@router.get("/events/{event_id}/attendees")
async def list_attendees(
    event_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
):
    message_id = int(event_id)
    stmt = (
        select(EventSignup.choice, EventSignup.user_id)
        .where(EventSignup.discord_message_id == message_id)
        .order_by(EventSignup.choice, EventSignup.created_at.asc())
    )
    rows = await db.execute(stmt)
    return [
        {"tag": tag, "userId": uid}
        for tag, uid in rows.all()
    ]


class RepeatPatchBody(BaseModel):
    repeat: Optional[str] = None
    time: datetime | None = None

    @field_validator("time", mode="before")
    @classmethod
    def _parse_time(cls, value: Any) -> Any:
        if value is None:
            return None
        if isinstance(value, datetime):
            dt = value
        else:
            s = value.replace("Z", "+00:00")
            if "." in s:
                head, tail = s.split(".", 1)
                if "+" in tail:
                    frac, tz = tail.split("+", 1)
                    s = f"{head}.{frac[:6]}+{tz}"
                elif "-" in tail:
                    frac, tz = tail.split("-", 1)
                    s = f"{head}.{frac[:6]}-{tz}"
                else:
                    s = f"{head}.{tail[:6]}"
            try:
                dt = datetime.fromisoformat(s)
            except ValueError:
                raise HTTPException(status_code=400, detail="Invalid time format")
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        else:
            dt = dt.astimezone(timezone.utc)
        return dt


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
        ev.next_post_at = body.time
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
        channel_id = ev.channel_id
        await db.delete(ev)
        await db.commit()
        await emit_event({"channel": channel_id, "op": "ed", "d": {"id": event_id}})
    return {"ok": True}
