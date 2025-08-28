from __future__ import annotations

import aiohttp
import discord
from discord import app_commands
from discord.ext import commands

from .setup_wizard import demi


class Events(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot


@demi.command(name="event", description="Create a simple event")
@app_commands.describe(
    title="Title of the event",
    time="Event time in ISO 8601 format (UTC)",
    description="Event description",
)
async def create_event(
    interaction: discord.Interaction, title: str, time: str, description: str
) -> None:
    host = interaction.client.cfg.server.host
    if host == "0.0.0.0":
        host = "localhost"
    base_url = f"http://{host}:{interaction.client.cfg.server.port}"
    body = {
        "channelId": str(interaction.channel_id or interaction.channel.id),
        "title": title,
        "time": time,
        "description": description,
    }
    headers = {
        "X-Api-Key": interaction.client.cfg.security.api_key,
        "X-Discord-Id": str(interaction.user.id),
    }
    async with aiohttp.ClientSession() as session:
        async with session.post(
            f"{base_url}/api/events", json=body, headers=headers
        ) as resp:
            if resp.status != 200:
                text = await resp.text()
                await interaction.response.send_message(
                    f"Failed to create event: {resp.status} {text}", ephemeral=True
                )
                return

    await interaction.response.send_message("Event created", ephemeral=True)


@app_commands.command(name="createevent", description="Create a simple event")
@app_commands.describe(
    title="Title of the event",
    time="Event time in ISO 8601 format (UTC)",
    description="Event description",
)
async def createevent(
    interaction: discord.Interaction, title: str, time: str, description: str
) -> None:
    await create_event.callback(interaction, title, time, description)


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(Events(bot))
    bot.tree.add_command(createevent)
