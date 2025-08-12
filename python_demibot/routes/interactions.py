from fastapi import APIRouter, Depends, HTTPException

from ..api import get_api_key_info, bot, db

router = APIRouter(prefix="/api/interactions")


@router.post("/")
async def post_interaction(payload: dict, info: dict = Depends(get_api_key_info)):
    channel_id = payload.get("channelId")
    message_id = payload.get("messageId")
    custom_id = payload.get("customId")
    try:
        channel = await bot.get_client().fetch_channel(channel_id)
        if not channel or channel.guild.id != int(info["serverId"]):
            raise HTTPException(status_code=400, detail="Invalid channel")
        message = await channel.fetch_message(message_id)

        view_store = bot._connection._view_store  # type: ignore[attr-defined]
        class DummyResponse:
            async def send_message(self, *args, **kwargs):
                pass

            async def edit_message(self, *args, **kwargs):
                pass

            async def defer(self, *args, **kwargs):
                pass

        class DummyInteraction:
            def __init__(self, message, user):
                self.message = message
                self.user = user
                self.channel = message.channel
                self.guild = getattr(message.channel, "guild", None)
                self.data = {"custom_id": custom_id, "component_type": 2}
                self.client = bot.get_client()
                self.response = DummyResponse()

        user = await bot.get_client().fetch_user(int(info["userId"]))
        interaction = DummyInteraction(message, user)
        view_store.dispatch_view(2, custom_id, interaction)
        if custom_id.startswith("attendance:"):
            status = custom_id.split(":", 1)[1]
            event = await db.get_event_by_message_id(message_id)
            if event:
                await db.set_event_attendance(event["id"], info["userId"], status)
        return {"ok": True}
    except HTTPException:
        raise
    except Exception as err:  # pragma: no cover - defensive
        raise HTTPException(status_code=500, detail={"ok": False}) from err
