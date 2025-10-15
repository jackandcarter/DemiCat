from __future__ import annotations

import json
from datetime import datetime

import discord
from discord.ext import commands
from sqlalchemy import delete, select
from sqlalchemy.exc import IntegrityError

from ...db.models import (
    Guild,
    GuildConfig,
    Membership,
    MembershipRole,
    Presence as DbPresence,
    Role,
    User,
)
from ...db.session import get_session
from ...http.ws import manager
from ..presence_store import Presence as StorePresence, set_presence
from ..utils import is_premium_subscriber_role


class PresenceTracker(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot

    def _status(self, member: discord.Member) -> str:
        status = str(member.status).lower()
        if status in {"offline", "invisible"}:
            return "offline"
        if status == "idle":
            return "idle"
        if status in {"dnd", "do_not_disturb"}:
            return "dnd"
        return "online"

    def _status_text(self, member: discord.Member) -> str | None:
        activities = getattr(member, "activities", None)
        if not activities:
            return None
        for activity in activities:
            if isinstance(activity, discord.CustomActivity):
                emoji = getattr(activity, "emoji", None)
                parts: list[str] = []
                if emoji is not None:
                    name = getattr(emoji, "name", None)
                    if name:
                        parts.append(str(name))
                text = getattr(activity, "name", None)
                if text:
                    parts.append(text)
                state = getattr(activity, "state", None)
                if state and state != text:
                    parts.append(state)
                if parts:
                    return " ".join(p for p in parts if p)
                return None
        return None

    async def _update(
        self, member: discord.Member, *, _retry: bool = False
    ) -> dict[str, str | None]:
        member_roles = [r for r in member.roles if r.name != "@everyone"]
        role_ids = [r.id for r in member_roles]
        role_details = [
            {"id": str(r.id), "name": r.name}
            for r in member_roles
        ]
        display_name = member.display_name or member.name
        avatar_url = str(member.display_avatar.url)
        banner_url: str | None = None
        banner_asset = getattr(member, "banner", None)
        if banner_asset is not None:
            asset_url = getattr(banner_asset, "url", None)
            banner_url = str(asset_url or banner_asset)

        def _extract_accent(value: object | None) -> int | None:
            if value is None:
                return None
            raw = getattr(value, "value", None)
            if isinstance(raw, int):
                return raw
            try:
                return int(value)
            except (TypeError, ValueError):
                return None

        accent_color_value = _extract_accent(getattr(member, "accent_color", None))

        if banner_url is None or accent_color_value is None:
            fallback_user: discord.abc.User | None = self.bot.get_user(member.id)
            if fallback_user is None:
                try:
                    fallback_user = await self.bot.fetch_user(member.id)
                except Exception:
                    fallback_user = None
            if fallback_user is not None:
                if banner_url is None:
                    fb_banner = getattr(fallback_user, "banner", None)
                    if fb_banner is not None:
                        asset_url = getattr(fb_banner, "url", None)
                        banner_url = str(asset_url or fb_banner)
                if accent_color_value is None:
                    accent_color_value = _extract_accent(
                        getattr(fallback_user, "accent_color", None)
                    )

        status_text = self._status_text(member)
        data = StorePresence(
            id=member.id,
            name=display_name,
            status=self._status(member),
            avatar_url=avatar_url,
            roles=role_ids,
            status_text=status_text,
            banner_url=banner_url,
            accent_color=accent_color_value,
        )
        set_presence(member.guild.id, data)
        async with get_session() as db:
            # Look up or create the internal guild record so that we can use its
            # database id for related records. The Membership and Presence tables
            # reference Guild.id rather than the Discord guild id.
            guild_res = await db.execute(
                select(Guild).where(Guild.discord_guild_id == member.guild.id)
            )
            guild_row = guild_res.scalar_one_or_none()
            if guild_row is None:
                guild_row = Guild(
                    discord_guild_id=member.guild.id, name=member.guild.name
                )
                db.add(guild_row)
                try:
                    await db.flush()
                except IntegrityError:
                    await db.rollback()
                    guild_res = await db.execute(
                        select(Guild).where(
                            Guild.discord_guild_id == member.guild.id
                        )
                    )
                    guild_row = guild_res.scalar_one()

            # Ensure we have an internal user record and use its database id for
            # memberships. Membership.user_id references users.id, not the
            # Discord snowflake.
            user_res = await db.execute(
                select(User).where(User.discord_user_id == member.id)
            )
            user_row = user_res.scalar_one_or_none()
            if user_row is None:
                user_row = User(
                    discord_user_id=member.id,
                    global_name=getattr(member, "global_name", None),
                    discriminator=getattr(member, "discriminator", None),
                )
                db.add(user_row)
                try:
                    await db.flush()
                except IntegrityError:
                    await db.rollback()
                    if _retry:
                        raise
                    return await self._update(member, _retry=True)
            else:
                user_row.global_name = getattr(member, "global_name", None)
                user_row.discriminator = getattr(member, "discriminator", None)

            mem_stmt = select(Membership).where(
                Membership.guild_id == guild_row.id,
                Membership.user_id == user_row.id,
            )
            mem_res = await db.execute(mem_stmt)
            mem = mem_res.scalars().first()
            if mem is None:
                mem = Membership(guild_id=guild_row.id, user_id=user_row.id)
                db.add(mem)
            mem.nickname = display_name
            mem.avatar_url = avatar_url
            mem.banner_url = banner_url
            mem.accent_color = accent_color_value

            cfg_res = await db.execute(
                select(GuildConfig).where(GuildConfig.guild_id == guild_row.id)
            )
            cfg = cfg_res.scalar_one_or_none()
            officer_role_ids = (
                {int(rid) for rid in cfg.officer_role_ids.split(",") if rid}
                if cfg and cfg.officer_role_ids
                else set()
            )
            chat_role_ids = (
                {int(rid) for rid in cfg.mention_role_ids.split(",") if rid}
                if cfg and cfg.mention_role_ids
                else set()
            )

            role_res = await db.execute(select(Role).where(Role.guild_id == guild_row.id))
            role_map = {role.discord_role_id: role for role in role_res.scalars()}

            for role in member_roles:
                is_officer = role.id in officer_role_ids
                is_chat = role.id in chat_role_ids
                mapped = role_map.get(role.id)
                premium_subscriber = is_premium_subscriber_role(role)
                if mapped is None:
                    mapped = Role(
                        guild_id=guild_row.id,
                        discord_role_id=role.id,
                        name=role.name,
                        position=role.position,
                        hoist=role.hoist,
                        premium_subscriber=premium_subscriber,
                        is_officer=is_officer,
                        is_chat=is_chat,
                    )
                    db.add(mapped)
                    await db.flush()
                    role_map[role.id] = mapped
                else:
                    mapped.name = role.name
                    mapped.is_officer = is_officer
                    mapped.is_chat = is_chat
                    mapped.position = role.position
                    mapped.hoist = role.hoist
                    mapped.premium_subscriber = premium_subscriber

            await db.execute(
                delete(MembershipRole).where(MembershipRole.membership_id == mem.id)
            )

            for role in member_roles:
                mapped = role_map.get(role.id)
                if mapped is not None:
                    db.add(MembershipRole(membership_id=mem.id, role_id=mapped.id))

            stmt = select(DbPresence).where(
                DbPresence.guild_id == guild_row.id,
                DbPresence.user_id == member.id,
            )
            res = await db.execute(stmt)
            row = res.scalars().first()
            if row is None:
                db.add(
                    DbPresence(
                        guild_id=guild_row.id,
                        user_id=member.id,
                        status=data.status,
                        status_text=data.status_text,
                    )
                )
            else:
                row.status = data.status
                row.status_text = data.status_text
                row.updated_at = datetime.utcnow()
            await db.commit()
        return {
            "id": str(member.id),
            "name": data.name,
            "status": data.status,
            "avatar_url": data.avatar_url,
            "roles": [str(r) for r in role_ids],
            "status_text": data.status_text,
            "role_details": role_details,
            "banner_url": banner_url,
            "accent_color": accent_color_value,
        }

    @commands.Cog.listener()
    async def on_ready(self) -> None:
        for guild in self.bot.guilds:
            for member in guild.members:
                await self._update(member)

    @commands.Cog.listener()
    async def on_presence_update(
        self, before: discord.Member, after: discord.Member
    ) -> None:
        payload = await self._update(after)
        await manager.broadcast_text(
            json.dumps(payload, ensure_ascii=False),
            after.guild.id,
            path="/ws/presences",
        )

    @commands.Cog.listener()
    async def on_member_update(
        self, before: discord.Member, after: discord.Member
    ) -> None:
        payload = await self._update(after)
        await manager.broadcast_text(
            json.dumps(payload, ensure_ascii=False),
            after.guild.id,
            path="/ws/presences",
        )


async def setup(bot: commands.Bot) -> None:
    await bot.add_cog(PresenceTracker(bot))
