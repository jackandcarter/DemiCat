from __future__ import annotations

import json
from typing import Any

from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext, api_key_auth, get_db
from ..schemas import TemplateDto, TemplatePayload, CamelModel
from ..ws import manager
from ...db.models import EventTemplate
from .events import create_event, CreateEventBody

router = APIRouter(prefix="/api")


class TemplateCreateBody(CamelModel):
    name: str
    description: str | None = None
    payload: TemplatePayload


class TemplateUpdateBody(CamelModel):
    name: str | None = None
    description: str | None = None
    payload: TemplatePayload | None = None


def _dump_payload(payload: TemplatePayload) -> str:
    return json.dumps(payload.model_dump(mode="json", by_alias=True, exclude_none=True))


def _template_to_dto(t: EventTemplate) -> TemplateDto:
    payload = TemplatePayload.model_validate(json.loads(t.payload_json))
    return TemplateDto(
        id=str(t.id),
        name=t.name,
        description=t.description,
        payload=payload,
    )


@router.post("/templates")
async def create_template(
    body: TemplateCreateBody,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> TemplateDto:
    tmpl = EventTemplate(
        guild_id=ctx.guild.id,
        name=body.name,
        description=body.description,
        payload_json=_dump_payload(body.payload),
    )
    db.add(tmpl)
    await db.commit()
    await db.refresh(tmpl)
    dto = _template_to_dto(tmpl)
    await manager.broadcast_text(
        json.dumps({
            "topic": "templates.updated",
            "payload": dto.model_dump(mode="json", by_alias=True, exclude_none=True),
        }),
        ctx.guild.id,
        path="/ws/templates",
    )
    return dto


@router.get("/templates")
async def list_templates(
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> list[TemplateDto]:
    result = await db.execute(
        select(EventTemplate).where(EventTemplate.guild_id == ctx.guild.id)
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
    if body.name is not None:
        tmpl.name = body.name
    if body.description is not None:
        tmpl.description = body.description
    if body.payload is not None:
        tmpl.payload_json = _dump_payload(body.payload)
    await db.commit()
    await db.refresh(tmpl)
    dto = _template_to_dto(tmpl)
    await manager.broadcast_text(
        json.dumps({
            "topic": "templates.updated",
            "payload": dto.model_dump(mode="json", by_alias=True, exclude_none=True),
        }),
        ctx.guild.id,
        path="/ws/templates",
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
    if tmpl and tmpl.guild_id == ctx.guild.id:
        await db.delete(tmpl)
        await db.commit()
        await manager.broadcast_text(
            json.dumps({
                "topic": "templates.updated",
                "payload": {"id": str(tid), "deleted": True},
            }),
            ctx.guild.id,
            path="/ws/templates",
        )
    return {"ok": True}


@router.post("/templates/{template_id}/post")
async def post_template(
    template_id: str,
    ctx: RequestContext = Depends(api_key_auth),
    db: AsyncSession = Depends(get_db),
) -> dict[str, Any]:
    tid = int(template_id)
    tmpl = await db.get(EventTemplate, tid)
    if not tmpl or tmpl.guild_id != ctx.guild.id:
        raise HTTPException(status_code=404, detail="Template not found")
    payload_dict = json.loads(tmpl.payload_json)
    body = CreateEventBody.model_validate(payload_dict)
    return await create_event(body=body, ctx=ctx, db=db)
