import base64
import io
from datetime import datetime
from typing import Dict, Any

import discord
from fastapi import APIRouter, Depends, HTTPException

from ..api import get_api_key_info, db, bot
from python_demibot.rate_limiter import enqueue

router = APIRouter(prefix="/api/events")


class AttendanceButton(discord.ui.Button):
    """Button used for tracking event attendance."""

    def __init__(self, status: str):
        label = status.title()
        styles = {
            "yes": discord.ButtonStyle.success,
            "maybe": discord.ButtonStyle.secondary,
            "no": discord.ButtonStyle.danger,
        }
        super().__init__(label=label, style=styles[status], custom_id=f"attendance:{status}")
        self.status = status

    async def callback(self, interaction: discord.Interaction):  # pragma: no cover - depends on Discord
        event = await db.get_event_by_message_id(interaction.message.id)
        if event:
            await db.set_event_attendance(event["id"], str(interaction.user.id), self.status)
        await interaction.response.defer()


class AttendanceView(discord.ui.View):
    def __init__(self, buttons: list[str]):
        super().__init__(timeout=None)
        for status in buttons:
            self.add_item(AttendanceButton(status))


def build_embed(data: Dict[str, Any], info: Dict[str, Any], include_image: bool = False) -> discord.Embed:
    embed = discord.Embed(
        title=data.get("title"),
        description=data.get("description"),
        url=data.get("url"),
        color=data.get("color"),
    )
    if data.get("time"):
        try:
            embed.timestamp = datetime.fromisoformat(data["time"])
        except Exception:
            try:
                embed.timestamp = datetime.fromtimestamp(float(data["time"]) / 1000)
            except Exception:
                pass
    if info.get("characterName"):
        embed.set_footer(text=info["characterName"])
    for field in data.get("fields") or []:
        embed.add_field(name=field.get("name"), value=field.get("value"), inline=field.get("inline", False))
    if data.get("thumbnailUrl"):
        embed.set_thumbnail(url=data["thumbnailUrl"])
    if data.get("authorName"):
        embed.set_author(name=data.get("authorName"), icon_url=data.get("authorIconUrl"))
    if data.get("imageUrl"):
        embed.set_image(url=data["imageUrl"])
    elif include_image:
        embed.set_image(url="attachment://image.png")
    return embed


@router.post("/")
async def create_event(payload: Dict[str, Any], info: Dict[str, Any] = Depends(get_api_key_info)):
    channel_id = payload.get("channelId")
    try:
        channel = await bot.get_client().fetch_channel(channel_id)
        if not channel or channel.guild.id != int(info["serverId"]):
            raise HTTPException(status_code=403, detail={"ok": False})
        embed = build_embed(payload, info, include_image=bool(payload.get("imageBase64")))
        files = []
        if payload.get("imageBase64"):
            data = base64.b64decode(payload["imageBase64"])
            files = [discord.File(io.BytesIO(data), filename="image.png")]
        buttons = payload.get("attendance") or ["yes", "maybe", "no"]
        view = AttendanceView(buttons)
        message = await enqueue(lambda: channel.send(embed=embed, files=files, view=view))
        settings = await db.get_server_settings(info["serverId"])
        events = set(settings.get("eventChannels", []))
        events.add(channel_id)
        await db.update_server_settings(info["serverId"], {"eventChannels": list(events)})
        bot.track_event_channel(channel_id)
        event_id = await db.save_event({
            "userId": info["userId"],
            "channelId": channel_id,
            "messageId": str(message.id),
            "title": payload.get("title"),
            "description": payload.get("description"),
            "time": payload.get("time"),
            "metadata": "image" if payload.get("imageBase64") else None,
        })
        return {"ok": True, "id": event_id}
    except HTTPException:
        raise
    except Exception as err:  # pragma: no cover - defensive
        raise HTTPException(status_code=500, detail={"ok": False}) from err


@router.patch("/{event_id}")
async def update_event(event_id: int, payload: Dict[str, Any], info: Dict[str, Any] = Depends(get_api_key_info)):
    try:
        existing = await db.get_event(event_id)
        if not existing:
            raise HTTPException(status_code=404, detail={"error": "Not found"})
        channel = await bot.get_client().fetch_channel(existing["channel_id"])
        if not channel or channel.guild.id != int(info["serverId"]):
            raise HTTPException(status_code=403, detail={"error": "Forbidden"})
        if str(existing["user_id"]) != info["userId"] and not info.get("isAdmin"):
            raise HTTPException(status_code=403, detail={"error": "Forbidden"})
        data = {
            "title": payload.get("title", existing["title"]),
            "description": payload.get("description", existing["description"]),
            "time": payload.get("time", existing["time"]),
            "color": payload.get("color"),
            "url": payload.get("url"),
            "fields": payload.get("fields"),
            "thumbnailUrl": payload.get("thumbnailUrl"),
            "authorName": payload.get("authorName"),
            "authorIconUrl": payload.get("authorIconUrl"),
            "imageUrl": payload.get("imageUrl"),
        }
        image_base64 = payload.get("imageBase64")
        embed = build_embed(data, info, include_image=bool(image_base64))
        files = []
        if image_base64:
            file_bytes = base64.b64decode(image_base64)
            files = [discord.File(io.BytesIO(file_bytes), filename="image.png")]
        message = await channel.fetch_message(existing["message_id"])
        await enqueue(lambda: message.edit(embed=embed, attachments=files))
        await db.update_event({
            "id": event_id,
            "title": data["title"],
            "description": data["description"],
            "time": data["time"],
            "metadata": "image" if image_base64 else existing.get("metadata"),
        })
        return {"ok": True}
    except HTTPException:
        raise
    except Exception as err:  # pragma: no cover - defensive
        raise HTTPException(status_code=500, detail={"ok": False}) from err


@router.delete("/{event_id}")
async def delete_event(event_id: int, info: Dict[str, Any] = Depends(get_api_key_info)):
    try:
        existing = await db.get_event(event_id)
        if not existing:
            raise HTTPException(status_code=404, detail={"error": "Not found"})
        channel = await bot.get_client().fetch_channel(existing["channel_id"])
        if not channel or channel.guild.id != int(info["serverId"]):
            raise HTTPException(status_code=403, detail={"error": "Forbidden"})
        if str(existing["user_id"]) != info["userId"] and not info.get("isAdmin"):
            raise HTTPException(status_code=403, detail={"error": "Forbidden"})
        title = existing["title"]
        if not title.endswith(" (Canceled)"):
            title = f"{title} (Canceled)"
        embed = build_embed({
            "title": title,
            "description": existing["description"],
            "time": existing["time"],
            "color": 0x808080,
        }, info)
        message = await channel.fetch_message(existing["message_id"])
        await enqueue(lambda: message.edit(embed=embed, attachments=[]))
        await db.update_event({
            "id": event_id,
            "title": title,
            "description": existing["description"],
            "time": existing["time"],
            "metadata": "canceled",
        })
        return {"ok": True}
    except HTTPException:
        raise
    except Exception as err:  # pragma: no cover - defensive
        raise HTTPException(status_code=500, detail={"ok": False}) from err
