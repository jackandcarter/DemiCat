"""Discord bot implementation used by the Python API wrapper.

This file mirrors the JavaScript bot located at
``discord-demibot/src/discord/index.js``.  The goal isn't feature parity but to
provide the same slash commands so the Python version can respond in a similar
fashion while still making use of the existing database layer.  Each command
returns an ephemeral response just like the JavaScript counterpart and performs
basic permission checks where appropriate.
"""

from __future__ import annotations

import asyncio
import secrets
from typing import Any, Dict, List

import discord
from discord import app_commands
from discord.ext import commands

from python_demibot import ws
from python_demibot.config import load_config
from python_demibot.database import Database
from python_demibot.rate_limiter import enqueue


class DemiBot(commands.Bot):
    """Minimal discord.py bot wrapper."""

    def __init__(self, token: str, db):
        intents = discord.Intents.default()
        intents.message_content = True
        intents.members = True
        intents.presences = True
        super().__init__(command_prefix="!", intents=intents)
        self.token = token
        self.db = db

        # Tracked channel IDs
        self.event_channels: List[str] = []
        self.fc_chat_channels: List[str] = []
        self.officer_chat_channels: List[str] = []

        # Cached payloads for WebSocket consumers
        self.message_cache: Dict[str, List[Dict[str, Any]]] = {}
        self.embed_cache: List[Dict[str, Any]] = []
        # Optional per-guild caches for quick lookup
        self.embed_cache_by_guild: Dict[str, List[Dict[str, Any]]] = {}

        # Register slash commands.  They are declared as methods below and added
        # to the command tree here so ``self`` is available inside callbacks.
        self.tree.add_command(self.link)
        self.tree.add_command(self.createevent)
        self.tree.add_command(self.generatekey)
        self.tree.add_command(self.demibot_setup)
        self.tree.add_command(self.demibot_resync)
        self.tree.add_command(self.demibot_embed)
        self.tree.add_command(self.demibot_reset)
        self.tree.add_command(self.demibot_settings)
        self.tree.add_command(self.demibot_clear)

    async def on_ready(self):
        print(f"Logged in as {self.user} (ID: {self.user.id})")

    async def start_bot(self) -> None:
        await self.start(self.token)

    async def setup_hook(self) -> None:  # pragma: no cover - discord.py hook
        """Sync application commands on start."""
        await self.tree.sync()

    # ------------------------------------------------------------------
    # Channel tracking helpers
    # ------------------------------------------------------------------

    def track_event_channel(self, channel_id: str) -> None:
        if channel_id not in self.event_channels:
            self.event_channels.append(channel_id)

    def track_fc_channel(self, channel_id: str) -> None:
        if channel_id not in self.fc_chat_channels:
            self.fc_chat_channels.append(channel_id)

    def track_officer_channel(self, channel_id: str) -> None:
        if channel_id not in self.officer_chat_channels:
            self.officer_chat_channels.append(channel_id)

    def get_client(self):
        """Return the underlying discord.py client."""
        return self

    def list_online_users(self) -> List[Dict[str, str]]:
        """Return a list of users that are currently online in any guild.

        Members with any status other than ``offline`` are considered online.
        """
        users: List[Dict[str, str]] = []
        for guild in self.guilds:
            for member in guild.members:
                if member.status != discord.Status.offline:
                    users.append({"id": str(member.id), "name": member.display_name})
        return users

    # ------------------------------------------------------------------
    # Message and embed mapping
    # ------------------------------------------------------------------

    @staticmethod
    def map_message(message: discord.Message) -> Dict[str, Any]:
        return {
            "id": str(message.id),
            "channelId": str(message.channel.id),
            "authorId": str(message.author.id),
            "authorName": message.author.display_name,
            "content": message.content,
            "mentions": [
                {"id": str(u.id), "name": u.display_name} for u in message.mentions
            ],
            "timestamp": message.created_at.isoformat(),
        }

    @staticmethod
    def map_embed(embed: discord.Embed, message: discord.Message) -> Dict[str, Any]:
        buttons = []
        for row in getattr(message, "components", []):
            for component in getattr(row, "children", []):
                if getattr(component, "type", None) == discord.ComponentType.button:
                    buttons.append(
                        {
                            "label": getattr(component, "label", None),
                            "customId": getattr(component, "custom_id", None),
                            "url": getattr(component, "url", None),
                        }
                    )

        return {
            "id": str(message.id),
            "channelId": str(message.channel.id),
            "serverId": str(message.guild.id),
            "timestamp": embed.timestamp.isoformat() if embed.timestamp else None,
            "authorName": embed.author.name if embed.author else None,
            "authorIconUrl": embed.author.icon_url if embed.author else None,
            "title": embed.title,
            "url": embed.url,
            "description": embed.description,
            "color": embed.color.value if embed.color else None,
            "fields": [
                {"name": f.name, "value": f.value, "inline": f.inline} for f in embed.fields
            ]
            if embed.fields
            else None,
            "thumbnailUrl": embed.thumbnail.url if embed.thumbnail else None,
            "imageUrl": embed.image.url if embed.image else None,
            "buttons": buttons if buttons else None,
        }

    # ------------------------------------------------------------------
    # Cache helpers
    # ------------------------------------------------------------------

    def _add_message(self, channel_id: str, msg: Dict[str, Any]) -> None:
        arr = self.message_cache.setdefault(channel_id, [])
        arr.append(msg)
        if len(arr) > 50:
            arr.pop(0)

    def _add_embed(self, embed: Dict[str, Any]) -> None:
        self.embed_cache.append(embed)
        if len(self.embed_cache) > 50:
            self.embed_cache.pop(0)

        guild_cache = self.embed_cache_by_guild.setdefault(embed["serverId"], [])
        guild_cache.append(embed)
        if len(guild_cache) > 50:
            guild_cache.pop(0)

    # ------------------------------------------------------------------
    # Discord event listeners
    # ------------------------------------------------------------------

    async def on_message(self, message: discord.Message) -> None:  # pragma: no cover - network
        await self.process_commands(message)

        channel_id = str(message.channel.id)

        if channel_id in self.event_channels and message.embeds:
            mapped = self.map_embed(message.embeds[0], message)
            self._add_embed(mapped)
            await ws.broadcast_embed(mapped)

        if channel_id in self.fc_chat_channels or channel_id in self.officer_chat_channels:
            mapped = self.map_message(message)
            self._add_message(channel_id, mapped)
            await ws.broadcast_message(mapped)

    # ------------------------------------------------------------------
    # Slash command implementations
    # ------------------------------------------------------------------

    @app_commands.command(name="link", description="Link your account")
    async def link(self, interaction: discord.Interaction) -> None:
        await enqueue(lambda: interaction.response.send_message("Link command received", ephemeral=True))

    @app_commands.command(name="createevent", description="Create an event")
    async def createevent(self, interaction: discord.Interaction) -> None:
        await enqueue(lambda: interaction.response.send_message("Create event command received", ephemeral=True))

    @app_commands.command(name="generatekey", description="Generate a key for DemiCat")
    async def generatekey(self, interaction: discord.Interaction) -> None:
        key = secrets.token_hex(16)
        guild_id = interaction.guild_id

        # Persist the key if the database implements the expected API.  This
        # mirrors the behaviour of the JavaScript bot but remains optional.
        if hasattr(self.db, "set_key"):
            await self.db.set_key(interaction.user.id, key, guild_id)

        embed = discord.Embed(title="DemiCat Link Key")
        embed.add_field(name="Key", value=key)
        embed.add_field(name="Sync Key", value=str(guild_id))

        try:
            await enqueue(lambda: interaction.user.send(embed=embed))
        except Exception:
            # Ignored: DM may fail if the user has DMs disabled
            pass
        await enqueue(lambda: interaction.response.send_message("Sent you a DM with your key!", ephemeral=True))

    @app_commands.command(name="demibot_setup", description="Set up DemiBot in this server")
    async def demibot_setup(self, interaction: discord.Interaction) -> None:
        if not interaction.user.guild_permissions.administrator:
            await enqueue(
                lambda: interaction.response.send_message(
                    "This command is restricted to administrators", ephemeral=True
                )
            )
            return

        await self._run_setup_wizard(interaction)

    @app_commands.command(name="demibot_resync", description="Resync DemiBot data")
    @app_commands.describe(users="Space-separated user mentions or IDs to resync")
    async def demibot_resync(self, interaction: discord.Interaction, users: str | None = None) -> None:
        if not interaction.user.guild_permissions.administrator:
            await enqueue(
                lambda: interaction.response.send_message(
                    "This command is restricted to administrators", ephemeral=True
                )
            )
            return

        members: list[discord.Member] = []
        if users:
            ids = [u.strip().strip("<@!>") for u in users.split()]  # mentions or raw IDs
            for uid in ids:
                try:
                    member = await interaction.guild.fetch_member(int(uid))  # type: ignore[arg-type]
                except Exception:
                    continue
                members.append(member)
        else:
            members = [m async for m in interaction.guild.fetch_members(limit=None)]  # type: ignore[assignment]

        updated = []
        for member in members:
            role_ids = [str(r.id) for r in member.roles if r != interaction.guild.default_role]
            if hasattr(self.db, "map_role_ids_to_tags") and hasattr(self.db, "set_user_roles"):
                tags = await self.db.map_role_ids_to_tags(interaction.guild_id, role_ids)
                await self.db.set_user_roles(interaction.guild_id, member.id, tags)
            updated.append(member.display_name)

        summary = (
            f"Updated {len(updated)} user(s): {', '.join(updated)}" if updated else "No users updated"
        )
        await enqueue(lambda: interaction.response.send_message(summary, ephemeral=True))

    @app_commands.command(name="demibot_embed", description="Create a DemiBot embed")
    async def demibot_embed(self, interaction: discord.Interaction) -> None:
        if not interaction.user.guild_permissions.administrator:
            await enqueue(
                lambda: interaction.response.send_message(
                    "This command is restricted to administrators", ephemeral=True
                )
            )
            return

        label = "Generate Key"
        if hasattr(self.db, "get_key"):
            existing = await self.db.get_key(interaction.user.id)
            if existing:
                label = "Show Key"

        embed = discord.Embed(title="DemiBot Key", description="Use the button below to generate or view your key.")
        view = discord.ui.View()
        view.add_item(
            discord.ui.Button(custom_id="demibot_key", label=label, style=discord.ButtonStyle.primary)
        )
        await enqueue(lambda: interaction.channel.send(embed=embed, view=view))
        await enqueue(lambda: interaction.response.send_message("DemiBot key embed created", ephemeral=True))

    async def _run_setup_wizard(self, interaction: discord.Interaction) -> None:
        """Interactive configuration wizard for channel and role setup."""

        async def ask_event_channels() -> list[str]:
            embed = discord.Embed(
                title="DemiBot Setup",
                description="Select event channel(s) and press **Next**",
            )

            class EventView(discord.ui.View):
                def __init__(self):
                    super().__init__(timeout=300)
                    self.channels: list[str] = []
                    self.add_item(self._select())
                    self.next_btn = discord.ui.Button(
                        label="Next", style=discord.ButtonStyle.primary, disabled=True
                    )
                    self.next_btn.callback = self._next
                    self.add_item(self.next_btn)

                def _select(self) -> discord.ui.ChannelSelect:
                    select = discord.ui.ChannelSelect(
                        channel_types=[discord.ChannelType.text], min_values=1, max_values=25
                    )

                    async def _callback(inter: discord.Interaction) -> None:
                        self.channels = [str(c.id) for c in select.values]
                        self.next_btn.disabled = False
                        await enqueue(lambda: inter.response.edit_message(view=self))

                    select.callback = _callback
                    return select

                async def _next(self, inter: discord.Interaction) -> None:
                    await enqueue(lambda: inter.response.defer())
                    self.stop()

            view = EventView()
            await enqueue(
                lambda: interaction.response.send_message(
                    embed=embed, view=view, ephemeral=True
                )
            )
            await view.wait()
            return view.channels

        async def ask_single_channel(message: str) -> str:
            embed = discord.Embed(title="DemiBot Setup", description=message)

            class ChannelView(discord.ui.View):
                def __init__(self):
                    super().__init__(timeout=300)
                    self.channel: str | None = None
                    self.add_item(self._select())
                    self.next_btn = discord.ui.Button(
                        label="Next", style=discord.ButtonStyle.primary, disabled=True
                    )
                    self.next_btn.callback = self._next
                    self.add_item(self.next_btn)

                def _select(self) -> discord.ui.ChannelSelect:
                    select = discord.ui.ChannelSelect(
                        channel_types=[discord.ChannelType.text], min_values=1, max_values=1
                    )

                    async def _callback(inter: discord.Interaction) -> None:
                        self.channel = str(select.values[0].id)
                        self.next_btn.disabled = False
                        await enqueue(lambda: inter.response.edit_message(view=self))

                    select.callback = _callback
                    return select

                async def _next(self, inter: discord.Interaction) -> None:
                    await enqueue(lambda: inter.response.defer())
                    self.stop()

            view = ChannelView()
            await enqueue(lambda: interaction.edit_original_response(embed=embed, view=view))
            await view.wait()
            return view.channel or ""

        async def ask_roles() -> list[str]:
            embed = discord.Embed(
                title="DemiBot Setup",
                description="Select officer role(s) and press **Save**",
            )

            class RoleView(discord.ui.View):
                def __init__(self):
                    super().__init__(timeout=300)
                    self.roles: list[str] = []
                    self.add_item(self._select())
                    self.save_btn = discord.ui.Button(
                        label="Save", style=discord.ButtonStyle.primary, disabled=True
                    )
                    self.save_btn.callback = self._save
                    self.add_item(self.save_btn)

                def _select(self) -> discord.ui.RoleSelect:
                    select = discord.ui.RoleSelect(min_values=1, max_values=25)

                    async def _callback(inter: discord.Interaction) -> None:
                        self.roles = [str(r.id) for r in select.values]
                        self.save_btn.disabled = False
                        await enqueue(lambda: inter.response.edit_message(view=self))

                    select.callback = _callback
                    return select

                async def _save(self, inter: discord.Interaction) -> None:
                    await enqueue(lambda: inter.response.defer())
                    self.stop()

            view = RoleView()
            await enqueue(lambda: interaction.edit_original_response(embed=embed, view=view))
            await view.wait()
            return view.roles

        event_channels = await ask_event_channels()
        fc_channel = await ask_single_channel("Select the FC chat channel and press **Next**")
        officer_channel = await ask_single_channel(
            "Select the officer chat channel and press **Next**"
        )
        officer_roles = await ask_roles()

        settings = {
            "eventChannels": event_channels,
            "fcChatChannel": fc_channel,
            "officerChatChannel": officer_channel,
        }
        if hasattr(self.db, "set_server_settings"):
            await self.db.set_server_settings(interaction.guild_id, settings)
        if hasattr(self.db, "set_officer_roles"):
            await self.db.set_officer_roles(interaction.guild_id, officer_roles)

        for ch in event_channels:
            self.track_event_channel(ch)
        if fc_channel:
            self.track_fc_channel(fc_channel)
        if officer_channel:
            self.track_officer_channel(officer_channel)

        summary = discord.Embed(title="DemiBot Setup", description="Configuration saved")
        await enqueue(lambda: interaction.edit_original_response(embed=summary, view=None))

    @app_commands.command(name="demibot_reset", description="Reset DemiBot data")
    async def demibot_reset(self, interaction: discord.Interaction) -> None:
        is_owner = interaction.guild.owner_id == interaction.user.id
        is_admin = interaction.user.guild_permissions.administrator
        if not (is_owner or is_admin):
            await enqueue(
                lambda: interaction.response.send_message(
                    "This command is restricted to administrators", ephemeral=True
                )
            )
            return

        if hasattr(self.db, "clear_server"):
            await self.db.clear_server(interaction.guild_id)
        await enqueue(lambda: interaction.response.send_message("DemiBot data reset.", ephemeral=True))

    @app_commands.command(name="demibot_settings", description="View or change DemiBot settings")
    async def demibot_settings(self, interaction: discord.Interaction) -> None:
        if not interaction.user.guild_permissions.administrator:
            await enqueue(
                lambda: interaction.response.send_message(
                    "This command is restricted to administrators", ephemeral=True
                )
            )
            return

        await self._run_setup_wizard(interaction)

    @app_commands.command(name="demibot_clear", description="Clear DemiBot configuration")
    async def demibot_clear(self, interaction: discord.Interaction) -> None:
        if interaction.guild.owner_id != interaction.user.id:
            await enqueue(
                lambda: interaction.response.send_message(
                    "This command is restricted to the server owner", ephemeral=True
                )
            )
            return

        if hasattr(self.db, "clear_server"):
            await self.db.clear_server(interaction.guild_id)
        await enqueue(lambda: interaction.response.send_message("All guild data has been purged.", ephemeral=True))


def main() -> None:
    config = load_config()
    db = Database(config)
    bot = DemiBot(config["discord_token"], db)
    asyncio.run(bot.start_bot())


if __name__ == "__main__":
    main()

