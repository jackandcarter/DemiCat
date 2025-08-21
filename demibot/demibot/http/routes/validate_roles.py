from __future__ import annotations

import logging
from fastapi import APIRouter, Depends, Request
from pydantic import BaseModel
from ..deps import RequestContext, api_key_auth

router = APIRouter()


class RolesResponse(BaseModel):
    roles: list[str] = []


@router.post("/validate")
async def validate(
    ctx: RequestContext = Depends(api_key_auth), request: Request = None
):
    client_ip = request.client.host if request and request.client else "unknown"
    logging.info("/validate request from %s", client_ip)
    response = {"ok": True}
    logging.info("/validate response status=200")
    return response


@router.post("/roles", response_model=RolesResponse)
async def roles(
    ctx: RequestContext = Depends(api_key_auth), request: Request = None
) -> RolesResponse:
    client_ip = request.client.host if request and request.client else "unknown"
    logging.info("/roles request from %s", client_ip)
    response = RolesResponse(roles=ctx.roles)
    logging.info("/roles response status=200")
    return response
