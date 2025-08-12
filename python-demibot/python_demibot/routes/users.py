from fastapi import APIRouter, Depends

from ..api import get_api_key_info, bot

router = APIRouter(prefix="/api/users")


@router.get("/")
async def list_users(info: dict = Depends(get_api_key_info)):
    return bot.list_online_users()
