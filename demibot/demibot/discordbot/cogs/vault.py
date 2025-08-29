from __future__ import annotations

import asyncio
import hashlib
import io
import json
import os
from datetime import datetime
from pathlib import Path
from zipfile import ZipFile, BadZipFile

import discord
from discord.ext import commands
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ...db.models import (
    AppearanceBundle,
    AppearanceBundleItem,
    Asset,
    AssetKind,
    Fc,
    Guild,
    IndexCheckpoint,
)
from ...db.session import get_session, init_db

WHITELIST = {
    ".zip",
    ".json",
    ".txt",
    ".png",
    ".jpg",
    ".jpeg",
    ".py",
}

INSTRUCTIONS = (
    "Upload asset bundles or files here. Allowed extensions: "
    + ", ".join(sorted(WHITELIST))
)


class Vault(commands.Cog):
    def __init__(self, bot: commands.Bot) -> None:
        self.bot = bot
        asyncio.create_task(init_db(bot.cfg.database.url))
        self.storage_path = Path(
            os.environ.get("ASSET_STORAGE_PATH", "assets")
        ).resolve()
        self.storage_path.mkdir(parents=True, exist_ok=True)
        self.vault_channels: dict[int, int] = {}

    async def cog_load(self) -> None:  # pragma: no cover - startup behaviour
        self.bot.loop.create_task(self._ensure_vault_channels())

    async def _ensure_vault_channels(self) -> None:  # pragma: no cover - startup
        if hasattr(self.bot, "wait_until_ready"):
            await self.bot.wait_until_ready()
        for guild in self.bot.guilds:
            await self._ensure_vault_channel(guild)

    async def _ensure_vault_channel(self, guild: discord.Guild) -> None:
        channel = discord.utils.get(guild.text_channels, name="vault")
        if channel is None:
            overwrites = {
                guild.default_role: discord.PermissionOverwrite(view_channel=False),
                guild.me: discord.PermissionOverwrite(view_channel=True),
            }
            try:
                channel = await guild.create_text_channel(
                    "vault", overwrites=overwrites, reason="Create vault channel"
                )
            except Exception:  # pragma: no cover - network failure
                return
        self.vault_channels[guild.id] = channel.id
        try:
            pins = await channel.pins()
        except Exception:  # pragma: no cover - network failure
            return
        if not any(INSTRUCTIONS in p.content for p in pins):
            try:
                msg = await channel.send(INSTRUCTIONS)
                await msg.pin()
            except Exception:  # pragma: no cover - network failure
                return

    @commands.Cog.listener()
    async def on_guild_join(self, guild: discord.Guild) -> None:
        await self._ensure_vault_channel(guild)

    @commands.Cog.listener()
    async def on_message(self, message: discord.Message) -> None:
        if message.guild is None or message.author.bot:
            return
        channel_id = self.vault_channels.get(message.guild.id)
        if channel_id is None or message.channel.id != channel_id:
            return
        if not message.attachments:
            return

        async for db in get_session():
            fc_id = await self._get_fc_id(db, message.guild)
            break

        errors: list[str] = []
        for attachment in message.attachments:
            ext = Path(attachment.filename).suffix.lower()
            if ext not in WHITELIST:
                errors.append(f"{attachment.filename}: extension not allowed")
                continue
            try:
                data = await attachment.read()
            except Exception:
                errors.append(f"{attachment.filename}: download failed")
                continue
            sha = hashlib.sha256(data).hexdigest()
            size = len(data)
            kind = self._determine_kind(ext, data)
            try:
                (self.storage_path / sha).write_bytes(data)
            except Exception:
                errors.append(f"{attachment.filename}: failed to store file")
                continue
            async for db in get_session():
                asset = await self._upsert_asset(
                    db, fc_id, kind, attachment.filename, sha, size
                )
                if kind is AssetKind.APPEARANCE:
                    await self._ensure_bundle(db, asset, data)
                await self._update_checkpoint(db, kind, asset.id)
                await db.commit()
                break
        if errors:
            await message.add_reaction("❌")
            await message.reply("; ".join(errors), mention_author=False)
        else:
            await message.add_reaction("✅")

    def _determine_kind(self, ext: str, data: bytes) -> AssetKind:
        if ext == ".py":
            return AssetKind.SCRIPT
        if ext == ".zip":
            try:
                with ZipFile(io.BytesIO(data)) as zf:
                    names = zf.namelist()
                for name in names:
                    if name.endswith(".json"):
                        return AssetKind.APPEARANCE
            except BadZipFile:
                pass
        return AssetKind.FILE

    async def _get_fc_id(self, db: AsyncSession, guild: discord.Guild) -> int | None:
        res = await db.execute(
            select(Fc.id)
            .join(Guild, Guild.id == Fc.id)
            .where(Guild.discord_guild_id == guild.id)
        )
        return res.scalar_one_or_none()

    async def _upsert_asset(
        self,
        db: AsyncSession,
        fc_id: int | None,
        kind: AssetKind,
        name: str,
        sha: str,
        size: int,
    ) -> Asset:
        result = await db.execute(select(Asset).where(Asset.hash == sha))
        asset = result.scalar_one_or_none()
        if asset is None:
            asset = Asset(
                fc_id=fc_id, kind=kind, name=name, hash=sha, size=size
            )
            db.add(asset)
            await db.flush()
        else:
            asset.name = name
            asset.size = size
            asset.deleted_at = None
        return asset

    async def _ensure_bundle(
        self, db: AsyncSession, asset: Asset, data: bytes
    ) -> None:
        info = self._parse_bundle_manifest(data)
        name = info.get("name") if isinstance(info, dict) else asset.name
        bundle = AppearanceBundle(name=name, fc_id=asset.fc_id)
        db.add(bundle)
        await db.flush()
        db.add(
            AppearanceBundleItem(bundle_id=bundle.id, asset_id=asset.id, quantity=1)
        )

    def _parse_bundle_manifest(self, data: bytes) -> dict:
        try:
            with ZipFile(io.BytesIO(data)) as zf:
                for name in zf.namelist():
                    if name.endswith("manifest.json") or name.endswith("bundle.json"):
                        with zf.open(name) as f:
                            return json.load(f)
        except Exception:
            return {}
        return {}

    async def _update_checkpoint(
        self, db: AsyncSession, kind: AssetKind, asset_id: int
    ) -> None:
        result = await db.execute(
            select(IndexCheckpoint).where(IndexCheckpoint.kind == kind)
        )
        cp = result.scalar_one_or_none()
        now = datetime.utcnow()
        if cp is None:
            db.add(
                IndexCheckpoint(
                    kind=kind, last_id=asset_id, last_generated_at=now
                )
            )
        else:
            cp.last_id = asset_id
            cp.last_generated_at = now
