from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import api_key_auth, get_db, RequestContext
from ...db.models import UserKey

router = APIRouter()


class ValidateRequest(BaseModel):
    key: str


@router.post("/validate")
async def validate(body: ValidateRequest, db: AsyncSession = Depends(get_db)) -> None:
    stmt = select(UserKey).where(UserKey.token == body.key, UserKey.enabled)
    result = await db.execute(stmt)
    key = result.scalar_one_or_none()
    if not key:
        raise HTTPException(status_code=401)
    return


class RolesResponse(BaseModel):
    roles: list[str] = []


@router.post("/roles", response_model=RolesResponse)
async def roles(ctx: RequestContext = Depends(api_key_auth)) -> RolesResponse:
    # For now, roles are not computed; placeholder empty list
    return RolesResponse(roles=[])
