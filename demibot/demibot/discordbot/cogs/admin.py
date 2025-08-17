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


async def _authorized_role_ids(guild_id: int) -> set[int]:
    """Return a set of Discord role IDs authorized for the guild."""
    async for db in get_session():
        guild_res = await db.execute(
            select(Guild).where(Guild.discord_guild_id == guild_id)
        )
        guild = guild_res.scalars().first()
        if guild is None:
            return set()
        result = await db.execute(
            select(Role.id).where(
                Role.guild_id == guild.id,
                (Role.is_officer.is_(True) | Role.is_chat.is_(True)),
            )
        )
        return {row[0] for row in result}
    return set()


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
    if interaction.guild is None:
        await interaction.response.send_message("Guild only", ephemeral=True)
        return

    allowed_roles = await _authorized_role_ids(interaction.guild.id)
    member_role_ids = {r.id for r in interaction.user.roles}
    if not (member_role_ids & allowed_roles):
        await interaction.response.send_message("You are not authorized", ephemeral=True)
        return

    cfg = getattr(interaction.client, "cfg", None)
    image_url = None
    if cfg is not None:
        security = getattr(cfg, "security", cfg)
        image_url = getattr(security, "sync_image_url", None)

    embed = discord.Embed(
        title="Generate Sync Key",
        description=(
            "Use the button below to generate a **Sync Key** for the DemiCat plugin. "
            "This key links your Discord account with the bot."
        ),
    )
    if image_url:
        embed.set_image(url=image_url)

    class KeyView(discord.ui.View):
        def __init__(self, allowed: set[int]) -> None:
            super().__init__(timeout=180)
            self.allowed = allowed

        @discord.ui.button(label="Generate Key", style=discord.ButtonStyle.primary)
        async def generate(
            self, button_inter: discord.Interaction, button: discord.ui.Button
        ) -> None:
            if not (set(r.id for r in button_inter.user.roles) & self.allowed):
                await button_inter.response.send_message(
                    "You are not authorized", ephemeral=True
                )
                return

            token = secrets.token_hex(16)
            try:
                async for db in get_session():
                    guild_res = await db.execute(
                        select(Guild).where(
                            Guild.discord_guild_id == button_inter.guild.id
                        )
                    )
                    guild = guild_res.scalars().first()
                    if guild is None:
                        guild = Guild(
                            discord_guild_id=button_inter.guild.id,
                            name=button_inter.guild.name,
                        )
                        db.add(guild)
                        await db.flush()

                    user_res = await db.execute(
                        select(User).where(
                            User.discord_user_id == button_inter.user.id
                        )
                    )
                    user = user_res.scalars().first()
                    if user is None:
                        user = User(
                            discord_user_id=button_inter.user.id,
                            global_name=button_inter.user.global_name,
                            discriminator=button_inter.user.discriminator,
                        )
                        db.add(user)
                        await db.flush()

                    db.add(UserKey(user_id=user.id, guild_id=guild.id, token=token))
                    await db.commit()
            except Exception:
                await button_inter.response.send_message(
                    "Failed to generate key", ephemeral=True
                )
                return

            await button_inter.response.send_message(
                f"Your sync key: {token}", ephemeral=True
            )

    view = KeyView(allowed_roles)
    await interaction.response.send_message(embed=embed, view=view)


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




class ConfigWizard(discord.ui.View):
    def __init__(self, guild: discord.Guild, title: str, final_label: str, success_message: str) -> None:
        super().__init__(timeout=300)
        self.guild = guild
        self.title = title
        self.final_label = final_label
        self.success_message = success_message
        self.step = 0
        self.event_channel_id: int | None = None
        self.fc_chat_channel_id: int | None = None
        self.officer_chat_channel_id: int | None = None
        self.officer_role_id: int | None = None
        self.chat_role_id: int | None = None
        self.channel_options = [
            discord.SelectOption(label=ch.name, value=str(ch.id))
            for ch in guild.text_channels
        ]
        self.role_options = [
            discord.SelectOption(label=r.name, value=str(r.id))
            for r in guild.roles
            if r.name != "@everyone"
        ]
        self.back_button = discord.ui.Button(label="Back", style=discord.ButtonStyle.secondary)
        self.back_button.callback = self.on_back
        self.next_button = discord.ui.Button(label="Next", style=discord.ButtonStyle.primary)
        self.next_button.callback = self.on_next
        self.finish_button = discord.ui.Button(label=final_label, style=discord.ButtonStyle.success)
        self.finish_button.callback = self.on_finish

    async def render(self, inter: discord.Interaction, *, initial: bool = False, followup: bool = False) -> None:
        self.clear_items()
        embed = discord.Embed(title=self.title, description=f"Step {self.step + 1} / 4")
        if self.step == 0:
            select = discord.ui.Select(placeholder="Select event channel", options=self.channel_options)
            if self.event_channel_id:
                for o in select.options:
                    if o.value == str(self.event_channel_id):
                        o.default = True
                        break
            async def cb(i: discord.Interaction) -> None:
                self.event_channel_id = int(select.values[0])
                await i.response.send_message("Event channel set", ephemeral=True)
            select.callback = cb
            self.add_item(select)
        elif self.step == 1:
            select = discord.ui.Select(placeholder="Select FC chat channel", options=self.channel_options)
            if self.fc_chat_channel_id:
                for o in select.options:
                    if o.value == str(self.fc_chat_channel_id):
                        o.default = True
                        break
            async def cb(i: discord.Interaction) -> None:
                self.fc_chat_channel_id = int(select.values[0])
                await i.response.send_message("FC chat channel set", ephemeral=True)
            select.callback = cb
            self.add_item(select)
        elif self.step == 2:
            select = discord.ui.Select(placeholder="Select officer chat channel", options=self.channel_options)
            if self.officer_chat_channel_id:
                for o in select.options:
                    if o.value == str(self.officer_chat_channel_id):
                        o.default = True
                        break
            async def cb(i: discord.Interaction) -> None:
                self.officer_chat_channel_id = int(select.values[0])
                await i.response.send_message("Officer chat channel set", ephemeral=True)
            select.callback = cb
            self.add_item(select)
        else:
            officer_select = discord.ui.Select(placeholder="Select officer role", options=self.role_options)
            if self.officer_role_id:
                for o in officer_select.options:
                    if o.value == str(self.officer_role_id):
                        o.default = True
                        break
            async def officer_cb(i: discord.Interaction) -> None:
                self.officer_role_id = int(officer_select.values[0])
                await i.response.send_message("Officer role set", ephemeral=True)
            officer_select.callback = officer_cb
            chat_select = discord.ui.Select(placeholder="Select chat role", options=self.role_options)
            if self.chat_role_id:
                for o in chat_select.options:
                    if o.value == str(self.chat_role_id):
                        o.default = True
                        break
            async def chat_cb(i: discord.Interaction) -> None:
                self.chat_role_id = int(chat_select.values[0])
                await i.response.send_message("Chat role set", ephemeral=True)
            chat_select.callback = chat_cb
            self.add_item(officer_select)
            self.add_item(chat_select)
        if self.step > 0:
            self.add_item(self.back_button)
        if self.step < 3:
            self.add_item(self.next_button)
        else:
            self.add_item(self.finish_button)
        if initial:
            await inter.response.send_message(embed=embed, view=self, ephemeral=True)
        elif followup:
            await inter.followup.edit_message(message_id=inter.message.id, embed=embed, view=self)
        else:
            await inter.response.edit_message(embed=embed, view=self)

    async def on_back(self, interaction: discord.Interaction) -> None:
        self.step = max(self.step - 1, 0)
        await self.render(interaction)

    async def on_next(self, interaction: discord.Interaction) -> None:
        if self.step == 0 and not self.event_channel_id:
            await interaction.response.send_message("Select an event channel", ephemeral=True)
            return
        if self.step == 1 and not self.fc_chat_channel_id:
            await interaction.response.send_message("Select an FC chat channel", ephemeral=True)
            return
        if self.step == 2 and not self.officer_chat_channel_id:
            await interaction.response.send_message("Select an officer chat channel", ephemeral=True)
            return
        self.step += 1
        await self.render(interaction)

    async def on_finish(self, interaction: discord.Interaction) -> None:
        if not all([
            self.event_channel_id,
            self.fc_chat_channel_id,
            self.officer_chat_channel_id,
            self.officer_role_id,
            self.chat_role_id,
        ]):
            await interaction.response.send_message("All selections are required", ephemeral=True)
            return
        try:
            async for db in get_session():
                guild_res = await db.execute(
                    select(Guild).where(Guild.discord_guild_id == self.guild.id)
                )
                guild = guild_res.scalars().first()
                if guild is None:
                    guild = Guild(
                        discord_guild_id=self.guild.id,
                        name=self.guild.name,
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
                config.event_channel_id = self.event_channel_id
                config.fc_chat_channel_id = self.fc_chat_channel_id
                config.officer_chat_channel_id = self.officer_chat_channel_id
                config.officer_role_id = self.officer_role_id
                config.chat_role_id = self.chat_role_id
                await db.execute(
                    delete(GuildChannel).where(
                        GuildChannel.guild_id == guild.id,
                        GuildChannel.kind.in_(["event", "fc_chat", "officer_chat"]),
                    )
                )
                db.add(
                    GuildChannel(
                        guild_id=guild.id,
                        channel_id=self.event_channel_id,
                        kind="event",
                    )
                )
                db.add(
                    GuildChannel(
                        guild_id=guild.id,
                        channel_id=self.fc_chat_channel_id,
                        kind="fc_chat",
                    )
                )
                db.add(
                    GuildChannel(
                        guild_id=guild.id,
                        channel_id=self.officer_chat_channel_id,
                        kind="officer_chat",
                    )
                )
                await db.commit()
        except Exception:
            await interaction.response.send_message("Failed to save settings", ephemeral=True)
            return
        await interaction.response.send_message(self.success_message, ephemeral=True)
        self.stop()
@demibot.command(name="settings", description="Open settings wizard")
async def settings_wizard(interaction: discord.Interaction) -> None:
    view = ConfigWizard(
        interaction.guild,
        title="Settings Wizard",
        final_label="Save",
        success_message="Settings saved",
    )
    await view.render(interaction, initial=True)

@demibot.command(name="setup", description="Initial setup wizard")
async def setup_wizard(interaction: discord.Interaction) -> None:
    if interaction.user.id != interaction.guild.owner_id:
        await interaction.response.send_message("Owner only", ephemeral=True)
        return

    view = ConfigWizard(
        interaction.guild,
        title="Setup Wizard",
        final_label="Finish",
        success_message="Setup complete",
    )
    await view.render(interaction, initial=True)

async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(Admin(bot))
    bot.tree.add_command(demibot)
