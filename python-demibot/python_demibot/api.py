from fastapi import Depends, FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
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


@app.middleware("http")
async def api_key_middleware(request: Request, call_next):
    path = request.url.path
    if path.startswith("/api") or path.startswith("/users"):
        key = request.headers.get("X-Api-Key")
        if not key:
            return JSONResponse({"error": "Missing X-Api-Key"}, status_code=401)
        info = await db.get_api_key(key)
        if not info:
            return JSONResponse({"error": "Invalid API key"}, status_code=401)
        request.state.api_key = info
    return await call_next(request)


def get_api_key_info(request: Request):
    info = getattr(request.state, "api_key", None)
    if not info:
        raise HTTPException(status_code=401, detail="Invalid API key")
    return info


async def require_admin(info: dict = Depends(get_api_key_info)):
    if not info.get("isAdmin"):
        raise HTTPException(status_code=403, detail="Forbidden")
    return info


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


@app.post("/api/admin/setup")
async def admin_setup(req: SetupRequest, info: dict = Depends(require_admin)):
    valid_types = {"event", "fc_chat", "officer_chat"}
    if req.type not in valid_types:
        raise HTTPException(status_code=400, detail="Invalid type")
    channel = await bot.get_client().fetch_channel(req.channelId)
    if not channel or not channel.is_text_based() or channel.guild.id != int(info["serverId"]):
        raise HTTPException(status_code=400, detail="Invalid channel")
    settings = await db.get_server_settings(info["serverId"])
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
    await db.set_server_settings(info["serverId"], settings)
    return {"ok": True}


from .routes import channels, messages, officer_messages, embeds, events, me, users

app.include_router(channels.router)
app.include_router(messages.router)
app.include_router(officer_messages.router)
app.include_router(embeds.router)
app.include_router(events.router)
app.include_router(me.router)
app.include_router(users.router)
