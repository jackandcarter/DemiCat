from __future__ import annotations

import aiohttp
import discord
from discord import app_commands
from discord.ext import commands

from .setup_wizard import demi
from ...db.models import RequestType, Urgency

class Requests(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot

async def _create_request(
    interaction: discord.Interaction,
    title: str,
    description: str | None,
    rtype: RequestType,
    urgency: Urgency,
) -> None:
    host = interaction.client.cfg.server.host
    if host == "0.0.0.0":
        host = "localhost"
    base_url = f"http://{host}:{interaction.client.cfg.server.port}"
    body = {
        "title": title,
        "description": description,
        "type": rtype,
        "urgency": urgency,
    }
    headers = {"X-Api-Key": interaction.client.cfg.security.api_key}
    async with aiohttp.ClientSession() as session:
        async with session.post(f"{base_url}/api/requests", json=body, headers=headers) as resp:
            if resp.status != 200:
                text = await resp.text()
                await interaction.response.send_message(
                    f"Failed to create request: {resp.status} {text}", ephemeral=True
                )
                return
    await interaction.response.send_message("Request submitted", ephemeral=True)

request_group = app_commands.Group(name="request", description="Create requests")

@request_group.command(name="craft", description="Request a crafted item")
@app_commands.describe(title="Item name", description="Details", urgency="Urgency")
async def request_craft(
    interaction: discord.Interaction,
    title: str,
    description: str | None = None,
    urgency: Urgency = Urgency.MEDIUM,
) -> None:
    await _create_request(interaction, title, description, RequestType.ITEM, urgency)

@request_group.command(name="dungeon", description="Request dungeon run")
@app_commands.describe(title="Dungeon name", description="Details", urgency="Urgency")
async def request_dungeon(
    interaction: discord.Interaction,
    title: str,
    description: str | None = None,
    urgency: Urgency = Urgency.MEDIUM,
) -> None:
    await _create_request(interaction, title, description, RequestType.RUN, urgency)

@demi.command(name="request", description="Create a request (legacy)")
@app_commands.describe(title="Title", description="Details", rtype="Type", urgency="Urgency")
async def legacy_request(
    interaction: discord.Interaction,
    title: str,
    description: str,
    rtype: RequestType,
    urgency: Urgency = Urgency.MEDIUM,
) -> None:
    await _create_request(interaction, title, description, rtype, urgency)

async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(Requests(bot))
    bot.tree.add_command(request_group)
