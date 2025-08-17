from __future__ import annotations

import secrets

import discord
from discord import app_commands
from discord.ext import commands
from sqlalchemy import delete, select

from ...db.models import (
    Guild,
    GuildChannel,
    GuildConfig,
    Membership,
    MembershipRole,
    Role,
    User,
    UserKey,
)
from ...db.session import get_session


demibot = app_commands.Group(name="demibot", description="Server administration")


class Admin(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot


@demibot.command(name="clear", description="Delete all user records for this guild")
async def clear_users(interaction: discord.Interaction) -> None:
    async for db in get_session():
        guild_res = await db.execute(
            select(Guild).where(Guild.discord_guild_id == interaction.guild.id)
        )
        guild = guild_res.scalars().first()
        if guild is None:
            await interaction.response.send_message(
                "No records for this guild", ephemeral=True
            )
            return
        member_ids = await db.execute(
            select(Membership.id).where(Membership.guild_id == guild.id)
        )
        member_ids = [m[0] for m in member_ids]
        if member_ids:
            await db.execute(
                delete(MembershipRole).where(
                    MembershipRole.membership_id.in_(member_ids)
                )
            )
        await db.execute(delete(Membership).where(Membership.guild_id == guild.id))
        await db.execute(delete(UserKey).where(UserKey.guild_id == guild.id))
        await db.commit()
    await interaction.response.send_message("Cleared user records", ephemeral=True)


@demibot.command(name="embed", description="Post key generation embed")
async def key_embed(interaction: discord.Interaction) -> None:
    embed = discord.Embed(
        title="Generate API Key",
        description="Click the button below to generate your key",
    )

    class KeyView(discord.ui.View):
        @discord.ui.button(label="Generate Key", style=discord.ButtonStyle.primary)
        async def generate(
            self, interaction_button: discord.Interaction, button: discord.ui.Button
        ) -> None:
            token = secrets.token_hex(16)
            try:
                async for db in get_session():
                    guild_res = await db.execute(
                        select(Guild).where(
                            Guild.discord_guild_id == interaction.guild.id
                        )
                    )
                    guild = guild_res.scalars().first()
                    if guild is None:
                        guild = Guild(
                            discord_guild_id=interaction.guild.id,
                            name=interaction.guild.name,
                        )
                        db.add(guild)
                        await db.flush()
                    user_res = await db.execute(
                        select(User).where(
                            User.discord_user_id == interaction_button.user.id
                        )
                    )
                    user = user_res.scalars().first()
                    if user is None:
                        user = User(
                            discord_user_id=interaction_button.user.id,
                            global_name=interaction_button.user.global_name,
                            discriminator=interaction_button.user.discriminator,
                        )
                        db.add(user)
                        await db.flush()
                    db.add(UserKey(user_id=user.id, guild_id=guild.id, token=token))
                    await db.commit()
            except Exception:
                await interaction_button.response.send_message(
                    "Failed to generate key", ephemeral=True
                )
                return
            try:
                await interaction_button.user.send(f"Your API key: {token}")
                await interaction_button.response.send_message(
                    "Sent you a DM with your key", ephemeral=True
                )
            except discord.Forbidden:
                await interaction_button.response.send_message(
                    "Unable to send DM", ephemeral=True
                )

    await interaction.response.send_message(embed=embed, view=KeyView())


@demibot.command(
    name="reset", description="Remove all guild configuration and cached users"
)
async def reset_guild(interaction: discord.Interaction) -> None:
    if interaction.user.id != interaction.guild.owner_id:
        await interaction.response.send_message("Owner only", ephemeral=True)
        return
    async for db in get_session():
        guild_res = await db.execute(
            select(Guild).where(Guild.discord_guild_id == interaction.guild.id)
        )
        guild = guild_res.scalars().first()
        if guild is None:
            await interaction.response.send_message(
                "No configuration for this guild", ephemeral=True
            )
            return
        member_ids = await db.execute(
            select(Membership.id).where(Membership.guild_id == guild.id)
        )
        member_ids = [m[0] for m in member_ids]
        if member_ids:
            await db.execute(
                delete(MembershipRole).where(
                    MembershipRole.membership_id.in_(member_ids)
                )
            )
        await db.execute(delete(Membership).where(Membership.guild_id == guild.id))
        await db.execute(delete(UserKey).where(UserKey.guild_id == guild.id))
        await db.execute(delete(Role).where(Role.guild_id == guild.id))
        await db.execute(delete(GuildChannel).where(GuildChannel.guild_id == guild.id))
        await db.execute(delete(GuildConfig).where(GuildConfig.guild_id == guild.id))
        await db.commit()
    await interaction.response.send_message("Guild data reset", ephemeral=True)


@demibot.command(name="resync", description="Refresh roles for stored keys")
async def resync_members(interaction: discord.Interaction) -> None:
    if not interaction.user.guild_permissions.administrator:
        await interaction.response.send_message("Admin only", ephemeral=True)
        return
    count = 0
    async for db in get_session():
        guild_res = await db.execute(
            select(Guild).where(Guild.discord_guild_id == interaction.guild.id)
        )
        guild = guild_res.scalars().first()
        if guild is None:
            await interaction.response.send_message(
                "No keys for this guild", ephemeral=True
            )
            return
        result = await db.execute(
            select(UserKey, User)
            .join(User, User.id == UserKey.user_id)
            .where(UserKey.guild_id == guild.id)
        )
        for key, user in result.all():
            member = interaction.guild.get_member(user.discord_user_id)
            if member:
                roles = [str(r.id) for r in member.roles if r.name != "@everyone"]
                key.roles_cached = ",".join(roles)
                count += 1
        await db.commit()
    await interaction.response.send_message(f"Resynced {count} members", ephemeral=True)


@demibot.command(name="settings", description="Open settings wizard")
async def settings_wizard(interaction: discord.Interaction) -> None:
    class SettingsView(discord.ui.View):
        def __init__(self) -> None:
            super().__init__(timeout=300)
            self.step = 0

        async def render(self, inter: discord.Interaction) -> None:
            embed = discord.Embed(
                title="Settings Wizard", description=f"Step {self.step + 1} / 3"
            )
            await inter.response.edit_message(embed=embed, view=self)

        @discord.ui.button(label="Back", style=discord.ButtonStyle.secondary)
        async def back(
            self, button_inter: discord.Interaction, button: discord.ui.Button
        ) -> None:
            self.step = max(self.step - 1, 0)
            await self.render(button_inter)

        @discord.ui.button(label="Next", style=discord.ButtonStyle.primary)
        async def next(
            self, button_inter: discord.Interaction, button: discord.ui.Button
        ) -> None:
            self.step = min(self.step + 1, 2)
            await self.render(button_inter)

        @discord.ui.button(label="Save", style=discord.ButtonStyle.success)
        async def save(
            self, button_inter: discord.Interaction, button: discord.ui.Button
        ) -> None:
            async for db in get_session():
                guild_res = await db.execute(
                    select(Guild).where(Guild.discord_guild_id == interaction.guild.id)
                )
                guild = guild_res.scalars().first()
                if guild is None:
                    guild = Guild(
                        discord_guild_id=interaction.guild.id,
                        name=interaction.guild.name,
                    )
                    db.add(guild)
                    await db.flush()
                config_res = await db.execute(
                    select(GuildConfig).where(GuildConfig.guild_id == guild.id)
                )
                config = config_res.scalars().first()
                if config is None:
                    config = GuildConfig(guild_id=guild.id)
                    db.add(config)
                await db.commit()
            await button_inter.response.send_message("Settings saved", ephemeral=True)
            self.stop()

    view = SettingsView()
    embed = discord.Embed(title="Settings Wizard", description="Step 1 / 3")
    await interaction.response.send_message(embed=embed, view=view, ephemeral=True)


@demibot.command(name="setup", description="Initial setup wizard")
async def setup_wizard(interaction: discord.Interaction) -> None:
    if interaction.user.id != interaction.guild.owner_id:
        await interaction.response.send_message("Owner only", ephemeral=True)
        return

    class SetupView(discord.ui.View):
        def __init__(self) -> None:
            super().__init__(timeout=300)
            self.step = 0

        async def render(self, inter: discord.Interaction) -> None:
            embed = discord.Embed(
                title="Setup Wizard", description=f"Step {self.step + 1} / 2"
            )
            await inter.response.edit_message(embed=embed, view=self)

        @discord.ui.button(label="Back", style=discord.ButtonStyle.secondary)
        async def back(
            self, button_inter: discord.Interaction, button: discord.ui.Button
        ) -> None:
            self.step = max(self.step - 1, 0)
            await self.render(button_inter)

        @discord.ui.button(label="Next", style=discord.ButtonStyle.primary)
        async def next(
            self, button_inter: discord.Interaction, button: discord.ui.Button
        ) -> None:
            self.step = min(self.step + 1, 1)
            await self.render(button_inter)

        @discord.ui.button(label="Finish", style=discord.ButtonStyle.success)
        async def finish(
            self, button_inter: discord.Interaction, button: discord.ui.Button
        ) -> None:
            async for db in get_session():
                guild_res = await db.execute(
                    select(Guild).where(Guild.discord_guild_id == interaction.guild.id)
                )
                guild = guild_res.scalars().first()
                if guild is None:
                    guild = Guild(
                        discord_guild_id=interaction.guild.id,
                        name=interaction.guild.name,
                    )
                    db.add(guild)
                    await db.flush()
                config_res = await db.execute(
                    select(GuildConfig).where(GuildConfig.guild_id == guild.id)
                )
                config = config_res.scalars().first()
                if config is None:
                    config = GuildConfig(guild_id=guild.id)
                    db.add(config)
                await db.commit()
            await button_inter.response.send_message("Setup complete", ephemeral=True)
            self.stop()

    view = SetupView()
    embed = discord.Embed(title="Setup Wizard", description="Step 1 / 2")
    await interaction.response.send_message(embed=embed, view=view, ephemeral=True)


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(Admin(bot))
    bot.tree.add_command(demibot)
