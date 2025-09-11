from __future__ import annotations

import json
from typing import Any
import logging

from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import JSONResponse
from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.ext.asyncio import AsyncSession
from pydantic import Field

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import (
    TemplateDto,
    TemplatePayload,
    CamelModel,
    EmbedDto,
    EmbedFieldDto,
    EmbedButtonDto,
)
from ..validation import validate_embed_payload
from ..ws import manager
from ...db.models import EventTemplate
from .events import create_event, CreateEventBody

router = APIRouter(prefix="/api")
logger = logging.getLogger(__name__)


class TemplateCreateBody(CamelModel):
    name: str
    description: str | None = None
    payload: TemplatePayload


class TemplateUpdateBody(CamelModel):
    name: str | None = None
    description: str | None = None
    payload: TemplatePayload | None = None


class TemplatePostOverrides(CamelModel):
    channel_id: str | None = Field(default=None, alias="channelId")
    time: str | None = None
    mentions: list[str] | None = None
    buttons: list[EmbedButtonDto] | None = None


def _dump_payload(payload: TemplatePayload) -> str:
    return json.dumps(payload.model_dump(mode="json", by_alias=True, exclude_none=True))


def _template_to_dto(t: EventTemplate) -> TemplateDto:
    payload = TemplatePayload.model_validate(json.loads(t.payload_json))
    return TemplateDto(
        id=str(t.id),
        name=t.name,
        description=t.description,
        payload=payload,
        updated_at=t.updated_at,
    )


@router.post("/templates")
async def create_template(
    body: TemplateCreateBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> TemplateDto:
    channel_id = (
        int(body.payload.channel_id)
        if body.payload.channel_id.isdigit()
        else None
    )
    logger.debug(
        "Incoming create_template request",
        extra={
            "event_id": None,
            "guild_id": ctx.guild.id,
            "channel_id": channel_id,
            "request": body.model_dump(mode="json", exclude_none=True),
        },
    )
    embed = EmbedDto(
        id="0",
        title=body.payload.title,
        description=body.payload.description,
        url=body.payload.url,
        image_url=body.payload.image_url,
        thumbnail_url=body.payload.thumbnail_url,
        color=body.payload.color,
        fields=body.payload.fields,
        buttons=body.payload.buttons,
        channel_id=channel_id,
    )
    validate_embed_payload(embed, body.payload.buttons or [])
    logger.debug(
        "Template payload validated",
        extra={
            "event_id": None,
            "guild_id": ctx.guild.id,
            "channel_id": channel_id,
        },
    )
    tmpl = EventTemplate(
        guild_id=ctx.guild.id,
        name=body.name,
        description=body.description,
        payload_json=_dump_payload(body.payload),
    )
    db.add(tmpl)
    try:
        await db.commit()
    except IntegrityError:
        await db.rollback()
        return JSONResponse({"error": "duplicate"}, status_code=409)
    await db.refresh(tmpl)
    dto = _template_to_dto(tmpl)
    try:
        await manager.broadcast_text(
            json.dumps(
                {
                    "topic": "templates.updated",
                    "payload": dto.model_dump(
                        mode="json", by_alias=True, exclude_none=True
                    ),
                }
            ),
            ctx.guild.id,
            path="/ws/templates",
        )
        logger.info(
            "Websocket broadcast successful",
            extra={
                "event_id": None,
                "guild_id": ctx.guild.id,
                "channel_id": channel_id,
            },
        )
    except Exception as exc:
        logger.error(
            "Websocket broadcast failed",
            extra={
                "event_id": None,
                "guild_id": ctx.guild.id,
                "channel_id": channel_id,
                "error": str(exc),
            },
        )
    return dto


@router.get("/templates")
async def list_templates(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> list[TemplateDto]:
    result = await db.execute(
        select(EventTemplate)
        .where(EventTemplate.guild_id == ctx.guild.id)
        .order_by(EventTemplate.updated_at.desc())
    )
    return [_template_to_dto(t) for t in result.scalars()]


@router.get("/templates/{template_id}")
async def get_template(
    template_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> TemplateDto:
    tid = int(template_id)
    tmpl = await db.get(EventTemplate, tid)
    if not tmpl or tmpl.guild_id != ctx.guild.id:
        raise HTTPException(status_code=404, detail="Template not found")
    return _template_to_dto(tmpl)


@router.patch("/templates/{template_id}")
async def update_template(
    template_id: str,
    body: TemplateUpdateBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> TemplateDto:
    tid = int(template_id)
    tmpl = await db.get(EventTemplate, tid)
    if not tmpl or tmpl.guild_id != ctx.guild.id:
        raise HTTPException(status_code=404, detail="Template not found")
    channel_id = None
    if body.payload and body.payload.channel_id and body.payload.channel_id.isdigit():
        channel_id = int(body.payload.channel_id)
    logger.debug(
        "Incoming update_template request",
        extra={
            "event_id": None,
            "guild_id": ctx.guild.id,
            "channel_id": channel_id,
            "request": body.model_dump(mode="json", exclude_none=True),
        },
    )
    if body.name is not None:
        tmpl.name = body.name
    if body.description is not None:
        tmpl.description = body.description
    if body.payload is not None:
        embed = EmbedDto(
            id="0",
            title=body.payload.title,
            description=body.payload.description,
            url=body.payload.url,
            image_url=body.payload.image_url,
            thumbnail_url=body.payload.thumbnail_url,
            color=body.payload.color,
            fields=body.payload.fields,
            buttons=body.payload.buttons,
            channel_id=channel_id,
        )
        validate_embed_payload(embed, body.payload.buttons or [])
        logger.debug(
            "Template payload validated",
            extra={
                "event_id": None,
                "guild_id": ctx.guild.id,
                "channel_id": channel_id,
            },
        )
        tmpl.payload_json = _dump_payload(body.payload)
    try:
        await db.commit()
    except IntegrityError:
        await db.rollback()
        return JSONResponse({"error": "duplicate"}, status_code=409)
    await db.refresh(tmpl)
    dto = _template_to_dto(tmpl)
    try:
        await manager.broadcast_text(
            json.dumps(
                {
                    "topic": "templates.updated",
                    "payload": dto.model_dump(
                        mode="json", by_alias=True, exclude_none=True
                    ),
                }
            ),
            ctx.guild.id,
            path="/ws/templates",
        )
        logger.info(
            "Websocket broadcast successful",
            extra={
                "event_id": None,
                "guild_id": ctx.guild.id,
                "channel_id": channel_id,
            },
        )
    except Exception as exc:
        logger.error(
            "Websocket broadcast failed",
            extra={
                "event_id": None,
                "guild_id": ctx.guild.id,
                "channel_id": channel_id,
                "error": str(exc),
            },
        )
    return dto


@router.delete("/templates/{template_id}")
async def delete_template(
    template_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    tid = int(template_id)
    tmpl = await db.get(EventTemplate, tid)
    channel_id = None
    if tmpl:
        try:
            channel_id = int(json.loads(tmpl.payload_json).get("channelId", 0))
        except Exception:
            channel_id = None
    logger.debug(
        "Incoming delete_template request",
        extra={
            "event_id": None,
            "guild_id": ctx.guild.id,
            "channel_id": channel_id,
        },
    )
    if tmpl and tmpl.guild_id == ctx.guild.id:
        await db.delete(tmpl)
        await db.commit()
        try:
            await manager.broadcast_text(
                json.dumps(
                    {
                        "topic": "templates.updated",
                        "payload": {"id": str(tid), "deleted": True},
                    }
                ),
                ctx.guild.id,
                path="/ws/templates",
            )
            logger.info(
                "Websocket broadcast successful",
                extra={
                    "event_id": None,
                    "guild_id": ctx.guild.id,
                    "channel_id": channel_id,
                },
            )
        except Exception as exc:
            logger.error(
                "Websocket broadcast failed",
                extra={
                    "event_id": None,
                    "guild_id": ctx.guild.id,
                    "channel_id": channel_id,
                    "error": str(exc),
                },
            )
    return {"ok": True}


@router.post("/templates/{template_id}/post")
async def post_template(
    template_id: str,
    overrides: TemplatePostOverrides | None = None,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    tid = int(template_id)
    tmpl = await db.get(EventTemplate, tid)
    if not tmpl or tmpl.guild_id != ctx.guild.id:
        raise HTTPException(status_code=404, detail="Template not found")
    payload_dict = json.loads(tmpl.payload_json)
    channel_id = payload_dict.get("channelId")
    if overrides:
        payload_dict.update(
            overrides.model_dump(mode="json", by_alias=True, exclude_none=True)
        )
        channel_id = overrides.channel_id or channel_id
    channel_id_int = int(channel_id) if channel_id and str(channel_id).isdigit() else None
    logger.debug(
        "Incoming post_template request",
        extra={
            "event_id": None,
            "guild_id": ctx.guild.id,
            "channel_id": channel_id_int,
            "request": overrides.model_dump(mode="json", exclude_none=True) if overrides else None,
        },
    )
    body = CreateEventBody.model_validate(payload_dict)
    return await create_event(body=body, ctx=ctx, db=db)
