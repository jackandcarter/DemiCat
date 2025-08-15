
from __future__ import annotations
from fastapi import APIRouter
from ._stores import USERS

router = APIRouter(prefix="/api")

@router.get("/users")
async def get_users():
    return [{"id": u.id, "name": u.name} for u in USERS]
