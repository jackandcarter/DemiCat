"""Discord bot implementation used by the Python API wrapper.

This file mirrors the JavaScript bot located at
``discord-demibot/src/discord/index.js``.  The goal isn't feature parity but to
provide the same slash commands so the Python version can respond in a similar
fashion while still making use of the existing database layer.  Each command
returns an ephemeral response just like the JavaScript counterpart and performs
basic permission checks where appropriate.
"""

from __future__ import annotations

import secrets

import discord
from discord import app_commands
from discord.ext import commands


class DemiBot(commands.Bot):
    """Minimal discord.py bot wrapper."""

    def __init__(self, token: str, db):
        intents = discord.Intents.default()
        intents.message_content = True
        super().__init__(command_prefix="!", intents=intents)
        self.token = token
        self.db = db

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
    # Slash command implementations
    # ------------------------------------------------------------------

    @app_commands.command(name="link", description="Link your account")
    async def link(self, interaction: discord.Interaction) -> None:
        await interaction.response.send_message("Link command received", ephemeral=True)

    @app_commands.command(name="createevent", description="Create an event")
    async def createevent(self, interaction: discord.Interaction) -> None:
        await interaction.response.send_message("Create event command received", ephemeral=True)

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
            await interaction.user.send(embed=embed)
        except Exception:
            # Ignored: DM may fail if the user has DMs disabled
            pass

        await interaction.response.send_message("Sent you a DM with your key!", ephemeral=True)

    @app_commands.command(name="demibot_setup", description="Set up DemiBot in this server")
    async def demibot_setup(self, interaction: discord.Interaction) -> None:
        await interaction.response.send_message("DemiBot setup started", ephemeral=True)

    @app_commands.command(name="demibot_resync", description="Resync DemiBot data")
    @app_commands.describe(users="Space-separated user mentions or IDs to resync")
    async def demibot_resync(self, interaction: discord.Interaction, users: str | None = None) -> None:
        if not interaction.user.guild_permissions.administrator:
            await interaction.response.send_message(
                "This command is restricted to administrators", ephemeral=True
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
            roles = [r.id for r in member.roles if r != interaction.guild.default_role]
            if hasattr(self.db, "set_user_roles"):
                await self.db.set_user_roles(interaction.guild_id, member.id, roles)
            updated.append(member.display_name)

        summary = (
            f"Updated {len(updated)} user(s): {', '.join(updated)}" if updated else "No users updated"
        )
        await interaction.response.send_message(summary, ephemeral=True)

    @app_commands.command(name="demibot_embed", description="Create a DemiBot embed")
    async def demibot_embed(self, interaction: discord.Interaction) -> None:
        if not interaction.user.guild_permissions.administrator:
            await interaction.response.send_message(
                "This command is restricted to administrators", ephemeral=True
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
        await interaction.channel.send(embed=embed, view=view)
        await interaction.response.send_message("DemiBot key embed created", ephemeral=True)

    @app_commands.command(name="demibot_reset", description="Reset DemiBot data")
    async def demibot_reset(self, interaction: discord.Interaction) -> None:
        is_owner = interaction.guild.owner_id == interaction.user.id
        is_admin = interaction.user.guild_permissions.administrator
        if not (is_owner or is_admin):
            await interaction.response.send_message(
                "This command is restricted to administrators", ephemeral=True
            )
            return

        if hasattr(self.db, "clear_server"):
            await self.db.clear_server(interaction.guild_id)
        await interaction.response.send_message("DemiBot data reset.", ephemeral=True)

    @app_commands.command(name="demibot_settings", description="View or change DemiBot settings")
    async def demibot_settings(self, interaction: discord.Interaction) -> None:
        if not interaction.user.guild_permissions.administrator:
            await interaction.response.send_message(
                "This command is restricted to administrators", ephemeral=True
            )
            return

        settings = {}
        if hasattr(self.db, "get_server_settings"):
            settings = await self.db.get_server_settings(interaction.guild_id)
        await interaction.response.send_message(f"Settings: {settings}", ephemeral=True)

    @app_commands.command(name="demibot_clear", description="Clear DemiBot configuration")
    async def demibot_clear(self, interaction: discord.Interaction) -> None:
        if interaction.guild.owner_id != interaction.user.id:
            await interaction.response.send_message(
                "This command is restricted to the server owner", ephemeral=True
            )
            return

        if hasattr(self.db, "clear_server"):
            await self.db.clear_server(interaction.guild_id)
        await interaction.response.send_message("All guild data has been purged.", ephemeral=True)

