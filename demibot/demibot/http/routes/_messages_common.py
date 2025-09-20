from __future__ import annotations

from datetime import datetime
import json
import io
import types
import logging
import asyncio
from typing import Sequence

import discord
from discord import ClientException
from fastapi import HTTPException, UploadFile
from pydantic import BaseModel, Field, ConfigDict, model_validator
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..deps import RequestContext
from ..schemas import (
    ChatMessage,
    AttachmentDto,
    MessageAuthor,
    Mention,
    ButtonComponentDto,
    ReactionDto,
    EmbedDto,
    MessageReferenceDto,
)
from ..discord_helpers import serialize_message
from ...bridge import BridgeUpload, build_bridge_message

from ..ws import manager
from ..chat_events import emit_event
from ...discordbot.utils import get_or_create_user
from ...db.models import (
    Message,
    Membership,
    GuildChannel,
    ChannelKind,
    PostedMessage,
)
from ..discord_client import discord_client


# Cache webhook URLs per channel to avoid recreation
_channel_webhooks: dict[int, str] = {}

MAX_ATTACHMENTS = 10
MAX_ATTACHMENT_SIZE = 25 * 1024 * 1024  # 25MB


def _make_discord_files(
    files: Sequence[discord.File | BridgeUpload] | None,
) -> list[discord.File] | None:
    """Materialise the provided file payloads into Discord file objects."""

    if not files:
        return None
    prepared: list[discord.File] = []
    for item in files:
        if isinstance(item, BridgeUpload):
            prepared.append(item.to_discord_file())
            continue
        try:
            item.reset()
        except Exception:
            pass
        prepared.append(item)
    return prepared


def _channel_supports_webhooks(channel: object | None) -> bool:
    """Return ``True`` when the channel can host a webhook."""

    if channel is None:
        return False

    thread_cls = getattr(discord, "Thread", None)
    if thread_cls is not None and isinstance(channel, thread_cls):
        return False

    messageable_cls = getattr(discord.abc, "Messageable", None)
    if messageable_cls is not None and isinstance(channel, messageable_cls):
        return True

    forum_cls = getattr(discord, "ForumChannel", None)
    if forum_cls is not None and isinstance(channel, forum_cls):
        return True

    if hasattr(channel, "create_webhook"):
        return True

    return False


async def _resolve_discord_channel(
    channel_id: int,
    *,
    guild_discord_id: int | None = None,
) -> discord.abc.Messageable | discord.Thread | None:
    """Attempt to resolve a Discord channel or thread by id."""

    if not discord_client:
        return None

    channel = discord_client.get_channel(channel_id)
    if channel is not None:
        return channel

    if guild_discord_id is not None:
        get_guild = getattr(discord_client, "get_guild", None)
        guild_obj = get_guild(guild_discord_id) if get_guild is not None else None
        if guild_obj is not None:
            channel = guild_obj.get_channel(channel_id)
            if channel is not None:
                return channel
            thread = guild_obj.get_thread(channel_id)
            if thread is not None:
                return thread

    try:
        return await discord_client.fetch_channel(channel_id)
    except discord.HTTPException as exc:  # pragma: no cover - network errors
        logging.warning(
            "fetch_channel(%s) failed: %s %s",
            channel_id,
            exc.status,
            exc.text,
        )
    except ClientException as exc:  # pragma: no cover - defensive
        logging.warning(
            "fetch_channel(%s) failed: %s",
            channel_id,
            exc,
        )
    except Exception:  # pragma: no cover - network errors
        logging.exception("fetch_channel(%s) unexpected error", channel_id)

    # Scan other guilds as a last resort in case the mapping is stale.
    for guild_obj in getattr(discord_client, "guilds", []):  # pragma: no cover - defensive
        thread = getattr(guild_obj, "get_thread", lambda _: None)(channel_id)
        if thread is not None:
            return thread
        channel = getattr(guild_obj, "get_channel", lambda _: None)(channel_id)
        if channel is not None:
            return channel

    return None


async def create_webhook_for_channel(
    *,
    channel: discord.abc.Messageable | None,
    channel_id: int,
    guild_id: int,
    guild_discord_id: int | None = None,
    db: AsyncSession,
) -> tuple[discord.Webhook | None, str | None, list[str]]:
    """Create a webhook for the given channel and persist its URL.

    Returns the created webhook object, its URL, and any error messages that
    should be surfaced to the caller.  The in-memory webhook cache is updated
    on success while callers are responsible for committing the session.
    """

    errors: list[str] = []
    resolved_channel = channel
    unsupported_channel = False

    if isinstance(resolved_channel, discord.Thread):
        resolved_channel = getattr(resolved_channel, "parent", None)

    if resolved_channel is not None and not _channel_supports_webhooks(resolved_channel):
        unsupported_channel = True
        resolved_channel = None

    if resolved_channel is None and discord_client:
        candidate: discord.abc.Messageable | discord.Thread | None
        candidate = discord_client.get_channel(channel_id)
        if candidate is None and guild_discord_id is not None:
            get_guild = getattr(discord_client, "get_guild", None)
            guild_obj = get_guild(guild_discord_id) if get_guild is not None else None
            if guild_obj is not None:
                candidate = guild_obj.get_channel(channel_id)
                if candidate is None:
                    candidate = guild_obj.get_thread(channel_id)
        if candidate is None:
            try:
                candidate = await discord_client.fetch_channel(channel_id)
            except discord.HTTPException as exc:  # pragma: no cover - network errors
                logging.warning(
                    "fetch_channel(%s) failed during webhook creation: %s %s",
                    channel_id,
                    exc.status,
                    exc.text,
                )
            except ClientException as exc:  # pragma: no cover - defensive
                logging.warning(
                    "fetch_channel(%s) failed during webhook creation: %s",
                    channel_id,
                    exc,
                )
            except Exception:  # pragma: no cover - network errors
                logging.exception(
                    "fetch_channel(%s) unexpected error during webhook creation",
                    channel_id,
                )
        if isinstance(candidate, discord.Thread):
            candidate = getattr(candidate, "parent", None)
        if candidate is not None and not _channel_supports_webhooks(candidate):
            unsupported_channel = True
            candidate = None
        resolved_channel = candidate

    if resolved_channel is None:
        if unsupported_channel:
            return None, None, errors
        errors.append("Webhook creation failed: channel not available")
        return None, None, errors

    try:
        created = await resolved_channel.create_webhook(name="DemiCat Relay")
    except discord.Forbidden:
        logging.warning(
            "Webhook creation forbidden",
            extra={"guild_id": guild_id, "channel_id": channel_id},
        )
        errors.append("Webhook creation failed: Manage Webhooks required")
        return None, None, errors
    except Exception as exc:  # pragma: no cover - network errors
        if isinstance(exc, discord.HTTPException):
            logging.exception(
                "create_webhook failed for guild %s channel %s: %s %s",
                guild_id,
                channel_id,
                exc.status,
                exc.text,
            )
            errors.append(
                f"Webhook creation failed: {exc.status} {_discord_error(exc)}"
            )
        else:
            logging.exception(
                "create_webhook failed for guild %s channel %s",
                guild_id,
                channel_id,
            )
            errors.append(f"Webhook creation failed: {exc}")
        return None, None, errors

    webhook_url = created.url
    _channel_webhooks[channel_id] = webhook_url
    result = await db.execute(
        select(GuildChannel).where(
            GuildChannel.guild_id == guild_id,
            GuildChannel.channel_id == channel_id,
        )
    )
    rows = result.scalars().all()
    if rows:
        for gc in rows:
            gc.webhook_url = webhook_url
    else:
        db.add(
            GuildChannel(
                guild_id=guild_id,
                channel_id=channel_id,
                kind=ChannelKind.CHAT,
                webhook_url=webhook_url,
            )
        )
    return created, webhook_url, errors


# Restrict everyone mentions but allow user and role pings
ALLOWED_MENTIONS = discord.AllowedMentions(
    users=True, roles=True, everyone=False
)


def _discord_error(exc: discord.HTTPException) -> str:
    """Return a human friendly discord error message."""
    txt = exc.text or ""
    try:
        data = json.loads(txt)
        if isinstance(data, dict):
            msg = data.get("message") or ""
            code = data.get("code")
            if code is not None:
                return f"{msg} (code {code})" if msg else f"code {code}"
            if msg:
                return msg
    except Exception:
        pass
    return txt or str(exc)


async def load_webhook_cache(db: AsyncSession) -> None:
    """Load webhook URLs from the database into the in-memory cache.

    When the Discord client is available, attempt to create webhooks for
    channels lacking a stored URL so that permission issues surface during
    startup.
    """

    result = await db.execute(
        select(
            GuildChannel.guild_id,
            GuildChannel.channel_id,
            GuildChannel.webhook_url,
        ).where(GuildChannel.webhook_url.is_not(None))
    )
    for _, channel_id, url in result.all():
        _channel_webhooks[channel_id] = url

    if not discord_client:
        return

    result = await db.execute(
        select(GuildChannel.guild_id, GuildChannel.channel_id).where(
            GuildChannel.webhook_url.is_(None)
        )
    )
    rows = result.all()
    updated = False
    for guild_id, channel_id in rows:
        channel = discord_client.get_channel(channel_id)
        if not channel or not isinstance(channel, discord.abc.Messageable):
            continue
        try:
            created = await channel.create_webhook(name="DemiCat Relay")
            _channel_webhooks[channel_id] = created.url
            gc = await db.scalar(
                select(GuildChannel).where(
                    GuildChannel.guild_id == guild_id,
                    GuildChannel.channel_id == channel_id,
                )
            )
            if gc:
                gc.webhook_url = created.url
                updated = True
        except Exception:
            logging.exception(
                "Failed to create webhook for channel %s", channel_id
            )
    if updated:
        await db.commit()


async def cleanup_webhooks(db: AsyncSession) -> None:
    """Remove webhook URLs for channels no longer present."""
    if not discord_client:
        return
    result = await db.execute(
        select(GuildChannel).where(GuildChannel.webhook_url.is_not(None))
    )
    stale: list[int] = []
    for row in result.scalars():
        if not discord_client.get_channel(row.channel_id):
            row.webhook_url = None
            stale.append(row.channel_id)
    if stale:
        await db.commit()
        for cid in stale:
            _channel_webhooks.pop(cid, None)


async def _send_via_webhook(
    *,
    channel: discord.abc.Messageable | None,
    channel_id: int,
    guild_id: int,
    guild_discord_id: int | None = None,
    content: str,
    username: str,
    avatar: str | None,
    files: Sequence[discord.File | BridgeUpload] | None,
    channel_kind: ChannelKind | None = None,
    embeds: Sequence[discord.Embed] | None = None,
    view: discord.ui.View | None = None,
    db: AsyncSession,
    thread: discord.abc.Snowflake | None = None,
) -> tuple[int | None, list[AttachmentDto] | None, list[str], str | None]:
    """Send a message via a channel webhook.

    ``channel`` should be the channel owning the webhook.  When ``thread`` is
    provided, the message will be sent to that thread using the parent
    channel's webhook.

    ``embeds`` and ``view`` are optional and allow rich messages to be sent via
    the webhook.  Returns the Discord message id, attachments, any error
    messages, and the webhook URL used when successful.
    """

    errors: list[str] = []
    webhook_url: str | None = _channel_webhooks.get(channel_id)
    if not webhook_url:
        stmt = select(GuildChannel.webhook_url).where(
            GuildChannel.guild_id == guild_id,
            GuildChannel.channel_id == channel_id,
            GuildChannel.webhook_url.is_not(None),
        )
        if channel_kind is not None:
            stmt = stmt.where(GuildChannel.kind == channel_kind)
        webhook_url = await db.scalar(stmt)
        if webhook_url:
            _channel_webhooks[channel_id] = webhook_url

    webhook = None
    if webhook_url:
        try:
            webhook = discord.Webhook.from_url(webhook_url, client=discord_client)
        except Exception as e:  # pragma: no cover - network errors
            logging.exception("failed to init webhook for channel %s", channel_id)
            errors.append(f"Webhook init failed: {e}")
            webhook = None

    if webhook is None:
        created, created_url, creation_errors = await create_webhook_for_channel(
            channel=channel,
            channel_id=channel_id,
            guild_id=guild_id,
            guild_discord_id=guild_discord_id,
            db=db,
        )
        errors.extend(creation_errors)
        if created_url:
            webhook_url = created_url
        if created is None:
            return None, None, errors, webhook_url
        webhook = created

    sent = None
    try:
        sent = await webhook.send(
            content,
            username=username,
            avatar_url=avatar,
            files=_make_discord_files(files),
            wait=True,
            allowed_mentions=ALLOWED_MENTIONS,
            embeds=list(embeds) if embeds else None,
            view=view,
            thread=thread,
        )
    except discord.HTTPException as e:  # pragma: no cover - network errors
        if e.status == 429:
            try:
                retry_after = float(getattr(e, "retry_after", 0)) or 1.0
            except Exception:
                retry_after = 1.0
            await asyncio.sleep(min(retry_after, 5.0))
            try:
                sent = await webhook.send(
                    content,
                    username=username,
                    avatar_url=avatar,
                    files=_make_discord_files(files),
                    wait=True,
                    allowed_mentions=ALLOWED_MENTIONS,
                    embeds=list(embeds) if embeds else None,
                    view=view,
                    thread=thread,
                )
            except Exception as e2:  # pragma: no cover - network errors
                e = e2
        if sent is None:
            logging.exception(
                "webhook.send failed for channel %s: %s %s",
                channel_id,
                e.status,
                e.text,
            )
            errors.append(
                f"Webhook send failed: {e.status} {e.text or _discord_error(e)}"
            )
    except Exception as e:  # pragma: no cover - network errors
        logging.exception("webhook.send failed for channel %s", channel_id)
        errors.append(f"Webhook send failed: {e}")

    if sent is None:
        created, created_url, retry_errors = await create_webhook_for_channel(
            channel=channel,
            channel_id=channel_id,
            guild_id=guild_id,
            guild_discord_id=guild_discord_id,
            db=db,
        )
        errors.extend(retry_errors)
        if created_url:
            webhook_url = created_url
        if created is None:
            return None, None, errors, webhook_url
        try:
            sent = await created.send(
                content,
                username=username,
                avatar_url=avatar,
                files=_make_discord_files(files),
                wait=True,
                allowed_mentions=ALLOWED_MENTIONS,
                embeds=list(embeds) if embeds else None,
                view=view,
                thread=thread,
            )
        except discord.Forbidden:
            logging.warning(
                "Webhook creation forbidden during retry",
                extra={"guild_id": guild_id, "channel_id": channel_id},
            )
            errors.append("Webhook retry failed: Manage Webhooks required")
            return None, None, errors, webhook_url
        except Exception as exc2:  # pragma: no cover - network errors
            if isinstance(exc2, discord.HTTPException):
                logging.exception(
                    "webhook.send failed after retry for guild %s channel %s: %s %s",
                    guild_id,
                    channel_id,
                    exc2.status,
                    exc2.text,
                )
                errors.append(
                    f"Webhook retry failed: {exc2.status} {exc2.text or _discord_error(exc2)}"
                )
            else:
                logging.exception(
                    "webhook.send failed after retry for guild %s channel %s",
                    guild_id,
                    channel_id,
                )
                errors.append(f"Webhook retry failed: {exc2}")
            return None, None, errors, webhook_url
        webhook = created

    discord_msg_id = getattr(sent, "id", None)
    attachments: list[AttachmentDto] | None = None
    if discord_msg_id is None:
        logging.warning(
            "webhook.send returned no id for channel %s", channel_id
        )
        errors.append(f"webhook.send returned no id for channel {channel_id}")
        return None, None, errors, webhook_url
    if sent.attachments:
        attachments = [
            AttachmentDto(
                url=a.url,
                filename=a.filename,
                contentType=a.content_type,
            )
            for a in sent.attachments
        ]

    return discord_msg_id, attachments, errors, webhook_url


class PostBody(BaseModel):
    channel_id: str = Field(alias="channelId")
    content: str
    use_character_name: bool | None = Field(default=False, alias="useCharacterName")
    message_reference: MessageReferenceDto | None = Field(
        default=None, alias="messageReference"
    )

    model_config = ConfigDict(populate_by_name=True)

    @model_validator(mode="before")
    @classmethod
    def _normalize_channel_id(cls, values: object) -> object:
        if isinstance(values, dict):
            channel_value = values.get("channel_id")
            for alias in ("channelId", "channel"):
                if channel_value is None and values.get(alias) is not None:
                    channel_value = values[alias]
                values.pop(alias, None)
            if channel_value is not None:
                values["channel_id"] = channel_value
        return values


async def fetch_messages(
    channel_id: str,
    ctx: RequestContext,
    db: AsyncSession,
    *,
    is_officer: bool,
    limit: int | None = None,
    before: str | None = None,
    after: str | None = None,
) -> list[dict]:
    if is_officer and "officer" not in ctx.roles:
        raise HTTPException(status_code=403)
    try:
        cid = int(channel_id)
    except ValueError:
        raise HTTPException(status_code=400, detail="invalid channel id")

    before_int = int(before) if before is not None else None
    after_int = int(after) if after is not None else None
    stmt = select(Message).where(
        Message.channel_id == cid,
        Message.is_officer.is_(is_officer),
    )
    if before_int is not None:
        stmt = stmt.where(Message.discord_message_id < before_int)
    if after_int is not None:
        stmt = stmt.where(Message.discord_message_id > after_int)
    stmt = stmt.order_by(Message.created_at.desc())
    if limit is not None:
        stmt = stmt.limit(limit)
    result = await db.execute(stmt)
    rows = list(result.scalars())

    shortfall = max(0, (limit or 100) - len(rows))

    if shortfall > 0 and discord_client:
        channel = discord_client.get_channel(cid)
        if channel and isinstance(channel, discord.abc.Messageable):
            fetched: list[discord.Message] = []
            try:
                history_kwargs: dict[str, object] = {"limit": shortfall}
                if before_int is not None:
                    history_kwargs["before"] = discord.Object(id=before_int)
                if after_int is not None:
                    history_kwargs["after"] = discord.Object(id=after_int)
                async for msg in channel.history(**history_kwargs):
                    fetched.append(msg)
            except Exception:
                fetched = []

            inserted = False
            for msg in fetched:
                if await db.get(Message, msg.id):
                    continue
                dto, fragments = serialize_message(msg)
                author_avatar = getattr(msg.author, "display_avatar", None)
                user_kwargs: dict[str, object] = {
                    "discord_user_id": msg.author.id,
                    "guild_id": ctx.guild.id,
                }
                if hasattr(msg.author, "global_name"):
                    user_kwargs["global_name"] = getattr(msg.author, "global_name")
                if hasattr(msg.author, "discriminator"):
                    user_kwargs["discriminator"] = getattr(
                        msg.author, "discriminator"
                    )
                if author_avatar is not None:
                    user_kwargs["avatar_url"] = getattr(author_avatar, "url", None)
                user = await get_or_create_user(db, **user_kwargs)
                db.add(
                    Message(
                        discord_message_id=msg.id,
                        channel_id=msg.channel.id,
                        guild_id=ctx.guild.id,
                        author_id=user.id,
                        author_name=dto.author_name,
                        author_avatar_url=dto.author_avatar_url,
                        content_raw=msg.content,
                        content_display=msg.content,
                        content=dto.content,
                        attachments_json=fragments["attachments_json"],
                        mentions_json=fragments["mentions_json"],
                        author_json=fragments["author_json"],
                        embeds_json=fragments["embeds_json"],
                        reference_json=fragments["reference_json"],
                        components_json=fragments["components_json"],
                        reactions_json=fragments["reactions_json"],
                        edited_timestamp=dto.edited_timestamp,
                        is_officer=is_officer,
                        created_at=msg.created_at,
                    )
                )
                inserted = True
            if inserted:
                await db.commit()
                result = await db.execute(stmt)
                rows = list(result.scalars())

    rows.reverse()
    out: list[ChatMessage] = []
    for m in rows:
        attachments = None
        if m.attachments_json:
            try:
                data = json.loads(m.attachments_json)
                attachments = [AttachmentDto(**a) for a in data]
            except Exception:
                attachments = None

        mentions = None
        if m.mentions_json:
            try:
                data = json.loads(m.mentions_json)
                mentions = [Mention(**a) for a in data]
            except Exception:
                mentions = None

        author = None
        if m.author_json:
            try:
                author = MessageAuthor(**json.loads(m.author_json))
            except Exception:
                author = None

        embeds = None
        if m.embeds_json:
            try:
                data = json.loads(m.embeds_json)
                embeds = [EmbedDto(**e) for e in data]
            except Exception:
                embeds = None

        reference = None
        if m.reference_json:
            try:
                reference = MessageReferenceDto(**json.loads(m.reference_json))
            except Exception:
                reference = None

        components = None
        if m.components_json:
            try:
                data = json.loads(m.components_json)
                components = [ButtonComponentDto(**c) for c in data]
            except Exception:
                components = None

        reactions = None
        if m.reactions_json:
            try:
                data = json.loads(m.reactions_json)
                reactions = [ReactionDto(**a) for a in data]
            except Exception:
                reactions = None

        out.append(
            ChatMessage(
                id=str(m.discord_message_id),
                channel_id=str(m.channel_id),
                author_name=m.author_name,
                author_avatar_url=m.author_avatar_url,
                timestamp=m.created_at,
                content=m.content or m.content_display or m.content_raw,
                attachments=attachments,
                mentions=mentions,
                author=author,
                embeds=embeds,
                reference=reference,
                components=components,
                reactions=reactions,
                edited_timestamp=m.edited_timestamp,
                use_character_name=getattr(author, "use_character_name", False),
            )
        )
    return [o.model_dump(by_alias=True, exclude_none=True) for o in out]


async def save_message(
    body: PostBody,
    ctx: RequestContext,
    db: AsyncSession,
    *,
    channel_kind: ChannelKind,
    files: list[UploadFile] | None = None,
) -> dict:
    try:
        cid = int(body.channel_id)
    except ValueError:
        raise HTTPException(status_code=400, detail="invalid channel id")
    gc_kind = await db.scalar(
        select(GuildChannel.kind).where(
            GuildChannel.guild_id == ctx.guild.id,
            GuildChannel.channel_id == cid,
            GuildChannel.kind == channel_kind,
        )
    )
    if gc_kind is None:
        raise HTTPException(
            status_code=400,
            detail="channel not configured for this tab; re-run wizard or pick a configured channel",
        )
    is_officer = gc_kind == ChannelKind.OFFICER_CHAT
    if is_officer and "officer" not in ctx.roles:
        raise HTTPException(status_code=403)
    discord_msg_id: int | None = None
    attachments: list[AttachmentDto] | None = None
    channel = None
    thread_obj: discord.Thread | None = None
    base_channel: discord.abc.Messageable | None = None
    guild_discord_id = getattr(ctx.guild, "discord_guild_id", None)
    membership = await db.scalar(
        select(Membership).where(
            Membership.guild_id == ctx.guild.id,
            Membership.user_id == ctx.user.id,
        )
    )
    nickname = membership.nickname if membership else None
    avatar = membership.avatar_url if membership else None
    error_details: list[str] = []
    if discord_client:
        channel = await _resolve_discord_channel(
            cid, guild_discord_id=guild_discord_id
        )
    if channel is not None:
        logging.info(
            "Resolved channel %s as %s in guild %s",
            cid,
            channel.__class__.__name__,
            getattr(getattr(channel, "guild", None), "id", guild_discord_id or "unknown"),
        )
        if isinstance(channel, discord.Thread):
            thread_obj = channel
            if getattr(channel, "archived", False):
                raise HTTPException(status_code=400, detail="thread is archived")
            base_channel = getattr(channel, "parent", None)
            if base_channel is None:
                raise HTTPException(status_code=400, detail="parent channel not found")
        else:
            base_channel = channel

        if isinstance(base_channel, discord.CategoryChannel):
            raise HTTPException(status_code=400, detail="cannot post to a category")
        if isinstance(channel, discord.Thread):
            if not _channel_supports_webhooks(base_channel):
                raise HTTPException(status_code=400, detail="unsupported channel type")
        else:
            messageable_cls = getattr(discord.abc, "Messageable", None)
            if messageable_cls is not None and not isinstance(base_channel, messageable_cls):
                raise HTTPException(status_code=400, detail="unsupported channel type")
            forum_cls = getattr(discord, "ForumChannel", None)
            if forum_cls is not None and isinstance(base_channel, forum_cls):
                raise HTTPException(status_code=400, detail="unsupported channel type")
    uploads_data: list[tuple[str, bytes, str | None]] = []
    if files:
        if len(files) > MAX_ATTACHMENTS:
            raise HTTPException(status_code=400, detail="Too many attachments")
        for f in files:
            data = await f.read()
            if len(data) > MAX_ATTACHMENT_SIZE:
                raise HTTPException(status_code=400, detail=f"{f.filename} too large")
            uploads_data.append((f.filename or "file", data, getattr(f, "content_type", None)))

    bridge_content, embeds, uploads, nonce = build_bridge_message(
        content=body.content,
        user=ctx.user,
        membership=membership,
        channel_kind=gc_kind or channel_kind,
        use_character_name=bool(body.use_character_name),
        attachments=uploads_data,
    )

    username_base = nickname or (
        ctx.user.global_name or ("Officer" if is_officer else "Player")
    )
    username = f"{username_base}@FFXIV FC"
    if body.use_character_name and ctx.user.character_name:
        username = f"{username_base} / {ctx.user.character_name}@FFXIV FC"
    username = username[:80]

    discord_msg_id, attachments, webhook_errors, webhook_url = await _send_via_webhook(
        channel=base_channel,
        channel_id=getattr(base_channel, "id", cid),
        guild_id=ctx.guild.id,
        guild_discord_id=guild_discord_id,
        content=bridge_content,
        username=username,
        avatar=avatar,
        files=uploads,
        channel_kind=channel_kind,
        embeds=embeds,
        db=db,
        thread=thread_obj,
    )
    error_details.extend(webhook_errors)

    if discord_msg_id is None:
        target_channel = thread_obj or base_channel
        log_extra = {"guild_id": ctx.guild.id, "channel_id": cid}
        if target_channel and isinstance(target_channel, discord.abc.Messageable):
            try:
                fallback_files = _make_discord_files(uploads)
                sent = await target_channel.send(
                    bridge_content,
                    files=fallback_files,
                    embeds=list(embeds) if embeds else None,
                    allowed_mentions=ALLOWED_MENTIONS,
                )
                discord_msg_id = getattr(sent, "id", None)
                if discord_msg_id is None:
                    logging.warning(
                        "channel.send returned no id for channel %s",
                        cid,
                        extra=log_extra,
                    )
                elif sent.attachments:
                    attachments = [
                        AttachmentDto(
                            url=a.url,
                            filename=a.filename,
                            contentType=a.content_type,
                        )
                        for a in sent.attachments
                    ]
                if webhook_url is None:
                    webhook_url = _channel_webhooks.get(cid)
            except Exception as e:
                if isinstance(e, discord.HTTPException):
                    logging.error(
                        "channel.send failed for channel %s: %s %s",
                        cid,
                        e.status,
                        e.text,
                        extra=log_extra,
                    )
                    error_details.append(
                        f"Direct send failed: {e.status} {e.text or _discord_error(e)}"
                    )
                else:
                    logging.error(
                        "channel.send failed for channel %s",
                        cid,
                        extra=log_extra,
                    )
                    error_details.append(f"Direct send failed: {e}")
        else:
            if is_officer:
                logging.warning(
                    "Officer channel unresolved", extra=log_extra
                )
                raise HTTPException(
                    status_code=409,
                    detail={
                        "code": "OFFICER_CHANNEL_UNRESOLVED",
                        "message": "Officer channel could not be resolved",
                        "channelId": str(cid),
                    },
                )
            logging.warning("Failed to resolve channel", extra=log_extra)
            detail: dict[str, object] = {"message": "channel not found"}
            if error_details:
                detail["discord"] = error_details
            raise HTTPException(status_code=404, detail=detail)

    if discord_msg_id is None:
        logging.warning(
            "Failed to relay message to Discord for channel %s",
            cid,
            extra={"guild_id": ctx.guild.id, "channel_id": cid},
        )
        detail: dict[str, object] = {"message": "Failed to relay message to Discord"}
        if error_details:
            detail["discord"] = error_details
        raise HTTPException(status_code=502, detail=detail)

    mapping = await db.scalar(
        select(PostedMessage).where(
            PostedMessage.guild_id == ctx.guild.id,
            PostedMessage.local_message_id == discord_msg_id,
        )
    )
    if mapping:
        mapping.channel_id = cid
        mapping.discord_message_id = discord_msg_id
        mapping.webhook_url = webhook_url
    else:
        mapping = PostedMessage(
            guild_id=ctx.guild.id,
            channel_id=cid,
            local_message_id=discord_msg_id,
            discord_message_id=discord_msg_id,
            webhook_url=webhook_url,
        )
        db.add(mapping)
    display_name_base = nickname or (
        ctx.user.global_name or ("Officer" if is_officer else "Player")
    )
    display_name = display_name_base
    if body.use_character_name and ctx.user.character_name:
        display_name = f"{ctx.user.character_name} ({display_name_base})"
    author = MessageAuthor(
        id=str(ctx.user.id),
        name=display_name,
        avatar_url=avatar,
        use_character_name=body.use_character_name,
    )

    dummy = types.SimpleNamespace(
        id=discord_msg_id,
        channel=types.SimpleNamespace(id=cid),
        author=types.SimpleNamespace(
            id=ctx.user.id,
            display_name=display_name,
            name=display_name,
            display_avatar=None,
        ),
        content=bridge_content,
        attachments=[
            types.SimpleNamespace(
                url=a.url, filename=a.filename, content_type=a.contentType
            )
            for a in (attachments or [])
        ],
        mentions=[],
        embeds=list(embeds),
        reference=types.SimpleNamespace(
            message_id=int(body.message_reference.message_id),
            channel_id=int(body.message_reference.channel_id),
        )
        if body.message_reference
        else None,
        components=[],
        reactions=[],
        created_at=datetime.utcnow(),
        edited_at=None,
    )

    dto, fragments = serialize_message(dummy)
    dto.author = author
    dto.author_name = author.name
    dto.author_avatar_url = author.avatar_url
    dto.use_character_name = bool(body.use_character_name)
    fragments["author_json"] = author.model_dump_json(by_alias=True, exclude_none=True)
    if mapping:
        mapping.embed_json = fragments["embeds_json"]
        mapping.nonce = nonce

    msg = Message(
        discord_message_id=discord_msg_id,
        channel_id=cid,
        guild_id=ctx.guild.id,
        author_id=ctx.user.id,
        author_name=author.name,
        author_avatar_url=author.avatar_url,
        content_raw=body.content,
        content_display=body.content,
        content=bridge_content,
        author_json=fragments["author_json"],
        attachments_json=fragments["attachments_json"],
        mentions_json=fragments["mentions_json"],
        embeds_json=fragments["embeds_json"],
        reference_json=fragments["reference_json"],
        components_json=fragments["components_json"],
        reactions_json=fragments["reactions_json"],
        is_officer=is_officer,
    )
    db.add(msg)
    await db.commit()
    await db.refresh(msg)

    payload = dto.model_dump(mode="json", by_alias=True, exclude_none=True)
    await manager.broadcast_text(
        json.dumps(payload),
        ctx.guild.id,
        officer_only=is_officer,
        path="/ws/officer-messages" if is_officer else "/ws/messages",
    )
    await emit_event({"channel": str(cid), "op": "mc", "d": payload})
    result: dict[str, object] = {"ok": True, "id": str(discord_msg_id)}
    if error_details:
        result["detail"] = {"discord": error_details}
    return result


async def edit_message(
    channel_id: str,
    message_id: str,
    content: str,
    ctx: RequestContext,
    db: AsyncSession,
    *,
    is_officer: bool,
) -> dict:
    if is_officer and "officer" not in ctx.roles:
        raise HTTPException(status_code=403)
    msg = await db.get(Message, int(message_id))
    try:
        cid = int(channel_id)
    except ValueError:
        raise HTTPException(status_code=400, detail="invalid channel id")
    if not msg or msg.channel_id != cid or msg.is_officer != is_officer:
        raise HTTPException(status_code=404)
    if msg.author_id != ctx.user.id:
        raise HTTPException(status_code=403)
    membership = await db.scalar(
        select(Membership).where(
            Membership.guild_id == ctx.guild.id,
            Membership.user_id == ctx.user.id,
        )
    )
    author_model: MessageAuthor | None = None
    if msg.author_json:
        try:
            author_model = MessageAuthor(**json.loads(msg.author_json))
        except Exception:
            author_model = None
    use_character_name_flag = bool(getattr(author_model, "use_character_name", False))
    channel_kind_value = (
        ChannelKind.OFFICER_CHAT if is_officer else ChannelKind.FC_CHAT
    )

    mapping = await db.scalar(
        select(PostedMessage).where(
            PostedMessage.guild_id == ctx.guild.id,
            PostedMessage.discord_message_id == int(message_id),
        )
    )
    existing_nonce = getattr(mapping, "nonce", None) if mapping else None

    bridge_content, embeds, _, nonce = build_bridge_message(
        content=content,
        user=ctx.user,
        membership=membership,
        channel_kind=channel_kind_value,
        use_character_name=use_character_name_flag,
        attachments=None,
        nonce=existing_nonce,
    )

    now = datetime.utcnow()
    msg.content_raw = content
    msg.content_display = content
    msg.content = bridge_content
    msg.edited_timestamp = now

    dummy_embed = types.SimpleNamespace(
        id=int(message_id),
        channel=types.SimpleNamespace(id=cid),
        author=types.SimpleNamespace(
            id=ctx.user.id,
            display_name=msg.author_name,
            name=msg.author_name,
            display_avatar=None,
        ),
        content=bridge_content,
        attachments=[],
        mentions=[],
        embeds=list(embeds),
        reference=None,
        components=[],
        reactions=[],
        created_at=msg.created_at,
        edited_at=now,
    )

    _, embed_fragments = serialize_message(dummy_embed)
    msg.embeds_json = embed_fragments["embeds_json"]
    if mapping:
        mapping.embed_json = embed_fragments["embeds_json"]
        mapping.nonce = nonce
    await db.commit()

    if discord_client:
        webhook_done = False
        webhook_url = getattr(mapping, "webhook_url", None) if mapping else None
        if webhook_url:
            try:
                webhook = discord.Webhook.from_url(
                    webhook_url,
                    client=discord_client,
                )
                await webhook.edit_message(
                    int(message_id),
                    content=bridge_content,
                    embeds=list(embeds) if embeds else None,
                    allowed_mentions=ALLOWED_MENTIONS,
                )
                webhook_done = True
            except Exception:
                logging.exception(
                    "Webhook edit failed for guild %s channel %s message %s",
                    ctx.guild.id,
                    cid,
                    message_id,
                )
        if not webhook_done:
            channel = discord_client.get_channel(cid)
            if channel and isinstance(channel, discord.abc.Messageable):
                try:
                    discord_msg = await channel.fetch_message(int(message_id))
                    await discord_msg.edit(
                        content=bridge_content,
                        embeds=list(embeds) if embeds else None,
                    )
                except Exception:
                    pass
    attachments = None
    if msg.attachments_json:
        try:
            data = json.loads(msg.attachments_json)
            attachments = [AttachmentDto(**a) for a in data]
        except Exception:
            attachments = None

    mentions = None
    if msg.mentions_json:
        try:
            data = json.loads(msg.mentions_json)
            mentions = [Mention(**m) for m in data]
        except Exception:
            mentions = None

    author = None
    if msg.author_json:
        try:
            author = MessageAuthor(**json.loads(msg.author_json))
        except Exception:
            author = None

    embeds = None
    if msg.embeds_json:
        try:
            data = json.loads(msg.embeds_json)
            embeds = [EmbedDto(**e) for e in data]
        except Exception:
            embeds = None

    reference = None
    if msg.reference_json:
        try:
            reference = MessageReferenceDto(**json.loads(msg.reference_json))
        except Exception:
            reference = None

    components = None
    if msg.components_json:
        try:
            data = json.loads(msg.components_json)
            components = [ButtonComponentDto(**c) for c in data]
        except Exception:
            components = None

    reactions = None
    if msg.reactions_json:
        try:
            data = json.loads(msg.reactions_json)
            reactions = [ReactionDto(**r) for r in data]
        except Exception:
            reactions = None

    dto = ChatMessage(
        id=str(message_id),
        channel_id=str(cid),
        author_name=msg.author_name,
        author_avatar_url=msg.author_avatar_url,
        timestamp=msg.created_at,
        content=bridge_content,
        attachments=attachments,
        mentions=mentions,
        author=author,
        embeds=embeds,
        reference=reference,
        components=components,
        reactions=reactions,
        edited_timestamp=msg.edited_timestamp,
        use_character_name=getattr(author, "use_character_name", False),
    )
    payload = dto.model_dump(mode="json", by_alias=True, exclude_none=True)
    await manager.broadcast_text(
        json.dumps(payload),
        ctx.guild.id,
        officer_only=is_officer,
        path="/ws/officer-messages" if is_officer else "/ws/messages",
    )
    await emit_event({"channel": str(cid), "op": "mu", "d": payload})
    return {"ok": True}


async def delete_message(
    channel_id: str,
    message_id: str,
    ctx: RequestContext,
    db: AsyncSession,
    *,
    is_officer: bool,
) -> dict:
    if is_officer and "officer" not in ctx.roles:
        raise HTTPException(status_code=403)
    msg = await db.get(Message, int(message_id))
    try:
        cid = int(channel_id)
    except ValueError:
        raise HTTPException(status_code=400, detail="invalid channel id")
    if not msg or msg.channel_id != cid or msg.is_officer != is_officer:
        raise HTTPException(status_code=404)
    if msg.author_id != ctx.user.id:
        raise HTTPException(status_code=403)

    mapping = await db.scalar(
        select(PostedMessage).where(
            PostedMessage.guild_id == ctx.guild.id,
            PostedMessage.discord_message_id == int(message_id),
        )
    )
    webhook_url = getattr(mapping, "webhook_url", None) if mapping else None

    await db.delete(msg)
    if mapping:
        await db.delete(mapping)
    await db.commit()
    if discord_client:
        webhook_done = False
        if webhook_url:
            try:
                webhook = discord.Webhook.from_url(
                    webhook_url,
                    client=discord_client,
                )
                await webhook.delete_message(int(message_id))
                webhook_done = True
            except Exception:
                logging.exception(
                    "Webhook delete failed for guild %s channel %s message %s",
                    ctx.guild.id,
                    cid,
                    message_id,
                )
        if not webhook_done:
            channel = discord_client.get_channel(cid)
            if channel and isinstance(channel, discord.abc.Messageable):
                try:
                    discord_msg = await channel.fetch_message(int(message_id))
                    await discord_msg.delete()
                except Exception:
                    pass
    payload = {"id": str(message_id), "channelId": str(cid), "deleted": True}
    await manager.broadcast_text(
        json.dumps(payload),
        ctx.guild.id,
        officer_only=is_officer,
        path="/ws/officer-messages" if is_officer else "/ws/messages",
    )
    await emit_event({"channel": str(cid), "op": "md", "d": {"id": str(message_id)}})
    return {"ok": True}
