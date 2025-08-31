from __future__ import annotations

import logging
import secrets

import discord
from discord import app_commands
from discord.ext import commands
from datetime import datetime

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
    Asset,
    IndexCheckpoint,
    UserInstallation,
)
from ...db.session import get_session


logger = logging.getLogger(__name__)

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


@demibot.command(name="deleteasset", description="Soft delete an asset")
@app_commands.describe(asset_id="Asset identifier")
async def delete_asset_cmd(
    interaction: discord.Interaction, asset_id: int
) -> None:
    async for db in get_session():
        result = await db.execute(select(Asset).where(Asset.id == asset_id))
        asset = result.scalar_one_or_none()
        if asset is None:
            await interaction.response.send_message(
                "Asset not found", ephemeral=True
            )
            return
        asset.deleted_at = datetime.utcnow()
        await db.commit()
        break
    await interaction.response.send_message("Asset deleted", ephemeral=True)


@demibot.command(
    name="rebuildindex", description="Reset asset index and optionally forget users"
)
@app_commands.describe(forget="Forget user installations")
async def rebuild_index_cmd(
    interaction: discord.Interaction, forget: bool = False
) -> None:
    async for db in get_session():
        await db.execute(delete(IndexCheckpoint))
        if forget:
            await db.execute(delete(UserInstallation))
        await db.commit()
        break
    msg = "Index rebuilt"
    if forget:
        msg += " and user installations cleared"
    await interaction.response.send_message(msg, ephemeral=True)


@demibot.command(name="embed", description="Post key generation embed")
async def key_embed(interaction: discord.Interaction) -> None:
    if interaction.guild is None:
        await interaction.response.send_message("Guild only", ephemeral=True)
        return

    if (
        interaction.user.id != interaction.guild.owner_id
        and not interaction.user.guild_permissions.administrator
    ):
        allowed_roles = await _authorized_role_ids(interaction.guild.id)
        member_role_ids = {r.id for r in interaction.user.roles}
        if not (member_role_ids & allowed_roles):
            await interaction.response.send_message(
                "You are not authorized", ephemeral=True
            )
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
        def __init__(self) -> None:
            super().__init__(timeout=180)

        @discord.ui.button(label="Generate Key", style=discord.ButtonStyle.primary)
        async def generate(
            self, button_inter: discord.Interaction, button: discord.ui.Button
        ) -> None:
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
                        select(User).where(User.discord_user_id == button_inter.user.id)
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

                    member_roles = [
                        r for r in button_inter.user.roles if r.name != "@everyone"
                    ]
                    roles_cached = ",".join(str(r.id) for r in member_roles)

                    membership_res = await db.execute(
                        select(Membership).where(
                            Membership.guild_id == guild.id,
                            Membership.user_id == user.id,
                        )
                    )
                    membership = membership_res.scalars().first()
                    if membership is None:
                        membership = Membership(guild_id=guild.id, user_id=user.id)
                        db.add(membership)
                        await db.flush()

                    await db.execute(
                        delete(MembershipRole).where(
                            MembershipRole.membership_id == membership.id
                        )
                    )
                    member_role_ids = {r.id for r in member_roles}
                    stored_roles = await db.execute(
                        select(Role.id, Role.discord_role_id).where(
                            Role.guild_id == guild.id
                        )
                    )
                    role_map = {
                        discord_role_id: role_id
                        for role_id, discord_role_id in stored_roles
                    }
                    for role_id in member_role_ids:
                        mapped = role_map.get(role_id)
                        if mapped:
                            db.add(
                                MembershipRole(
                                    membership_id=membership.id, role_id=mapped
                                )
                            )

                    db.add(
                        UserKey(
                            user_id=user.id,
                            guild_id=guild.id,
                            token=token,
                            roles_cached=roles_cached,
                        )
                    )
                    await db.commit()
            except Exception:
                logger.exception("Sync key generation failed")
                await button_inter.response.send_message(
                    "Failed to generate key", ephemeral=True
                )
                return

            await button_inter.response.send_message(
                f"Your sync key: {token}", ephemeral=True
            )

    view = KeyView()
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
        stored_role_res = await db.execute(
            select(Role.id, Role.discord_role_id).where(Role.guild_id == guild.id)
        )
        role_map = {
            discord_role_id: role_id
            for role_id, discord_role_id in stored_role_res.all()
        }
        result = await db.execute(
            select(UserKey, User)
            .join(User, User.id == UserKey.user_id)
            .where(UserKey.guild_id == guild.id)
        )
        for key, user in result.all():
            member = interaction.guild.get_member(user.discord_user_id)
            if member:
                member_roles = [r for r in member.roles if r.name != "@everyone"]
                member_role_ids = {r.id for r in member_roles}
                membership_res = await db.execute(
                    select(Membership).where(
                        Membership.guild_id == guild.id,
                        Membership.user_id == user.id,
                    )
                )
                membership = membership_res.scalars().first()
                if membership is None:
                    membership = Membership(guild_id=guild.id, user_id=user.id)
                    db.add(membership)
                    await db.flush()
                await db.execute(
                    delete(MembershipRole).where(
                        MembershipRole.membership_id == membership.id
                    )
                )
                for role in member_roles:
                    if role.id not in role_map:
                        db_role = Role(
                            guild_id=guild.id,
                            discord_role_id=role.id,
                            name=role.name,
                        )
                        db.add(db_role)
                        await db.flush()
                        role_map[role.id] = db_role.id
                for role_id in member_role_ids:
                    db.add(
                        MembershipRole(
                            membership_id=membership.id, role_id=role_map[role_id]
                        )
                    )
                key.roles_cached = ",".join(str(rid) for rid in member_role_ids)
                count += 1
        await db.commit()
    await interaction.response.send_message(f"Resynced {count} members", ephemeral=True)


class ConfigWizard(discord.ui.View):
    def __init__(
        self, guild: discord.Guild, title: str, final_label: str, success_message: str
    ) -> None:
        super().__init__(timeout=300)
        self.guild = guild
        self.title = title
        self.final_label = final_label
        self.success_message = success_message
        self.step = 0
        self.event_channel_ids: list[int] = []
        self.fc_chat_channel_ids: list[int] = []
        self.officer_chat_channel_ids: list[int] = []
        self.officer_role_id: int | None = None
        self.channel_options = [
            discord.SelectOption(label=ch.name, value=str(ch.id))
            for ch in guild.text_channels
        ]
        self.role_options = [
            discord.SelectOption(label=r.name, value=str(r.id))
            for r in guild.roles
            if r.name != "@everyone"
        ]
        self.back_button = discord.ui.Button(
            label="Back", style=discord.ButtonStyle.secondary
        )
        self.back_button.callback = self.on_back
        self.next_button = discord.ui.Button(
            label="Next", style=discord.ButtonStyle.primary
        )
        self.next_button.callback = self.on_next
        self.finish_button = discord.ui.Button(
            label=final_label, style=discord.ButtonStyle.success
        )
        self.finish_button.callback = self.on_finish

    async def render(
        self,
        inter: discord.Interaction,
        *,
        initial: bool = False,
        followup: bool = False,
    ) -> None:
        self.clear_items()
        embed = discord.Embed(title=self.title, description=f"Step {self.step + 1} / 4")
        if self.step == 0:
            select = discord.ui.Select(
                placeholder="Select event channels",
                options=self.channel_options,
                max_values=min(25, len(self.channel_options)),
            )
            if self.event_channel_ids:
                for o in select.options:
                    if int(o.value) in self.event_channel_ids:
                        o.default = True

            async def cb(i: discord.Interaction) -> None:
                self.event_channel_ids = [int(v) for v in select.values]
                await i.response.defer()

            select.callback = cb
            self.add_item(select)
        elif self.step == 1:
            select = discord.ui.Select(
                placeholder="Select FC chat channels",
                options=self.channel_options,
                max_values=min(25, len(self.channel_options)),
            )
            if self.fc_chat_channel_ids:
                for o in select.options:
                    if int(o.value) in self.fc_chat_channel_ids:
                        o.default = True

            async def cb(i: discord.Interaction) -> None:
                self.fc_chat_channel_ids = [int(v) for v in select.values]
                await i.response.defer()

            select.callback = cb
            self.add_item(select)
        elif self.step == 2:
            select = discord.ui.Select(
                placeholder="Select officer chat channels",
                options=self.channel_options,
                max_values=min(25, len(self.channel_options)),
            )
            if self.officer_chat_channel_ids:
                for o in select.options:
                    if int(o.value) in self.officer_chat_channel_ids:
                        o.default = True

            async def cb(i: discord.Interaction) -> None:
                self.officer_chat_channel_ids = [int(v) for v in select.values]
                await i.response.defer()

            select.callback = cb
            self.add_item(select)
        else:
            officer_select = discord.ui.Select(
                placeholder="Select officer role",
                options=self.role_options,
            )
            if self.officer_role_id:
                for o in officer_select.options:
                    if o.value == str(self.officer_role_id):
                        o.default = True
                        break

            async def officer_cb(i: discord.Interaction) -> None:
                self.officer_role_id = int(officer_select.values[0])
                await i.response.defer()

            officer_select.callback = officer_cb
            self.add_item(officer_select)
        if self.step > 0:
            self.add_item(self.back_button)
        if self.step < 3:
            self.add_item(self.next_button)
        else:
            self.add_item(self.finish_button)
        if initial:
            await inter.response.send_message(embed=embed, view=self, ephemeral=True)
        elif followup:
            await inter.followup.edit_message(
                message_id=inter.message.id, embed=embed, view=self
            )
        else:
            await inter.response.edit_message(embed=embed, view=self)

    async def on_back(self, interaction: discord.Interaction) -> None:
        self.step = max(self.step - 1, 0)
        await self.render(interaction)

    async def on_next(self, interaction: discord.Interaction) -> None:
        if self.step == 0 and not self.event_channel_ids:
            await interaction.response.send_message(
                "Select at least one event channel", ephemeral=True
            )
            return
        if self.step == 1 and not self.fc_chat_channel_ids:
            await interaction.response.send_message(
                "Select at least one FC chat channel", ephemeral=True
            )
            return
        if self.step == 2 and not self.officer_chat_channel_ids:
            await interaction.response.send_message(
                "Select at least one officer chat channel", ephemeral=True
            )
            return
        self.step += 1
        await self.render(interaction)

    async def on_finish(self, interaction: discord.Interaction) -> None:
        if not all(
            [
                self.event_channel_ids,
                self.fc_chat_channel_ids,
                self.officer_chat_channel_ids,
                self.officer_role_id,
            ]
        ):
            await interaction.response.send_message(
                "All selections are required", ephemeral=True
            )
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
                config.officer_role_id = self.officer_role_id
                role_name = self.guild.get_role(self.officer_role_id)
                role_name = role_name.name if role_name else "Officer"
                role_res = await db.execute(
                    select(Role).where(
                        Role.guild_id == guild.id,
                        Role.discord_role_id == self.officer_role_id,
                    )
                )
                role = role_res.scalars().first()
                if role is None:
                    db.add(
                        Role(
                            guild_id=guild.id,
                            name=role_name,
                            discord_role_id=self.officer_role_id,
                            is_officer=True,
                        )
                    )
                else:
                    role.name = role_name
                    role.is_officer = True
                await db.execute(
                    delete(GuildChannel).where(
                        GuildChannel.guild_id == guild.id,
                        GuildChannel.kind.in_(["event", "fc_chat", "officer_chat"]),
                    )
                )
                channel_name_lookup = {
                    int(opt.value): opt.label for opt in self.channel_options
                }
                for cid in self.event_channel_ids:
                    channel = self.guild.get_channel(cid)
                    db.add(
                        GuildChannel(
                            guild_id=guild.id,
                            channel_id=cid,
                            kind="event",
                            name=channel.name if channel else channel_name_lookup.get(cid),
                        )
                    )
                for cid in self.fc_chat_channel_ids:
                    channel = self.guild.get_channel(cid)
                    db.add(
                        GuildChannel(
                            guild_id=guild.id,
                            channel_id=cid,
                            kind="fc_chat",
                            name=channel.name if channel else channel_name_lookup.get(cid),
                        )
                    )
                for cid in self.officer_chat_channel_ids:
                    channel = self.guild.get_channel(cid)
                    db.add(
                        GuildChannel(
                            guild_id=guild.id,
                            channel_id=cid,
                            kind="officer_chat",
                            name=channel.name if channel else channel_name_lookup.get(cid),
                        )
                    )
                await db.commit()
        except Exception:
            await interaction.response.send_message(
                "Failed to save settings", ephemeral=True
            )
            return
        summary = (
            f"{self.success_message}\n"
            f"Event channels: {', '.join(f'<#{c}>' for c in self.event_channel_ids)}\n"
            f"FC chat channels: {', '.join(f'<#{c}>' for c in self.fc_chat_channel_ids)}\n"
            f"Officer chat channels: {', '.join(f'<#{c}>' for c in self.officer_chat_channel_ids)}\n"
            f"Officer role: <@&{self.officer_role_id}>"
        )
        await interaction.response.send_message(summary, ephemeral=True)
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
