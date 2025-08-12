from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

from .config import load_config
from .database import Database
from .discord_bot import DemiBot
from . import ws

config = load_config()
db = Database(config)
bot = DemiBot(config["discord_token"], db)

app = FastAPI()
ws.start(app, bot)


class ValidateRequest(BaseModel):
    key: str | None = None
    syncKey: str | None = None
    characterName: str | None = None


class RolesRequest(BaseModel):
    key: str


@app.on_event("startup")
async def startup() -> None:
    await db.connect()


@app.on_event("shutdown")
async def shutdown() -> None:
    await db.close()


@app.post("/validate")
async def validate(req: ValidateRequest):
    if (req.key and req.key == config["user_key"]) or (req.syncKey and req.syncKey == config["sync_key"]):
        guild = {"id": config.get("guild_id", ""), "name": config.get("guild_name", "")}
        return {"userKey": config["user_key"], "guild": guild}
    raise HTTPException(status_code=401, detail="invalid key")


@app.post("/roles")
async def roles(req: RolesRequest):
    roles = await db.get_user_roles(req.key)
    if roles is None:
        raise HTTPException(status_code=401, detail="invalid key")
    return {"roles": roles}


class SetupRequest(BaseModel):
    channelId: str
    type: str


@app.post("/admin/setup")
async def admin_setup(req: SetupRequest):
    valid_types = {"event", "fc_chat", "officer_chat"}
    if req.type not in valid_types:
        raise HTTPException(status_code=400, detail="Invalid type")
    settings = await db.get_server_settings(config["guild_id"])
    if req.type == "event":
        events = set(settings.get("eventChannels", []))
        events.add(req.channelId)
        settings["eventChannels"] = list(events)
        bot.track_event_channel(req.channelId)
    elif req.type == "fc_chat":
        settings["fcChatChannel"] = req.channelId
        bot.track_fc_channel(req.channelId)
    elif req.type == "officer_chat":
        settings["officerChatChannel"] = req.channelId
        bot.track_officer_channel(req.channelId)
    await db.set_server_settings(config["guild_id"], settings)
    return {"ok": True}
