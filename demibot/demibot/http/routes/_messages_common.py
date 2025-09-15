from __future__ import annotations

from datetime import datetime
import json
import io
import types
import logging
import asyncio

import discord
from fastapi import HTTPException, UploadFile
from pydantic import BaseModel, Field
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

from ..ws import manager
from ...db.models import Message, Membership, GuildChannel, ChannelKind
from ..discord_client import discord_client


# Cache webhook URLs per channel to avoid recreation
_channel_webhooks: dict[int, str] = {}

MAX_ATTACHMENTS = 10
MAX_ATTACHMENT_SIZE = 25 * 1024 * 1024  # 25MB

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
    content: str,
    username: str,
    avatar: str | None,
    files: list[discord.File] | None,
    db: AsyncSession,
    thread: discord.abc.Snowflake | None = None,
) -> tuple[int | None, list[AttachmentDto] | None, list[str]]:
    """Send a message via a channel webhook.

    ``channel`` should be the channel owning the webhook.  When ``thread`` is
    provided, the message will be sent to that thread using the parent
    channel's webhook.

    Returns the Discord message id and attachments on success, otherwise
    a list of error messages describing the failure.
    """

    errors: list[str] = []
    webhook_url = _channel_webhooks.get(channel_id)
    if not webhook_url:
        webhook_url = await db.scalar(
            select(GuildChannel.webhook_url).where(
                GuildChannel.guild_id == guild_id,
                GuildChannel.channel_id == channel_id,
            )
        )
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

    if webhook is None and channel is not None:
        try:
            created = await channel.create_webhook(name="DemiCat Relay")
            webhook_url = created.url
            _channel_webhooks[channel_id] = webhook_url
            gc = await db.scalar(
                select(GuildChannel).where(
                    GuildChannel.guild_id == guild_id,
                    GuildChannel.channel_id == channel_id,
                )
            )
            if gc:
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
            webhook = created
        except Exception as e:  # pragma: no cover - network errors
            if isinstance(e, discord.HTTPException):
                logging.exception(
                    "create_webhook failed for channel %s: %s %s",
                    channel_id,
                    e.status,
                    e.text,
                )
                errors.append(
                    f"Webhook creation failed: {e.status} {_discord_error(e)}"
                )
            else:
                logging.exception(
                    "create_webhook failed for channel %s", channel_id
                )
                errors.append(f"Webhook creation failed: {e}")
            return None, None, errors
    elif webhook is None:
        errors.append("Webhook creation failed: channel not available")
        return None, None, errors

    sent = None
    try:
        sent = await webhook.send(
            content,
            username=username,
            avatar_url=avatar,
            files=files,
            wait=True,
            allowed_mentions=ALLOWED_MENTIONS,
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
                    files=files,
                    wait=True,
                    allowed_mentions=ALLOWED_MENTIONS,
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
        try:
            created = await channel.create_webhook(name="DemiCat Relay")
            webhook_url = created.url
            _channel_webhooks[channel_id] = webhook_url
            gc = await db.scalar(
                select(GuildChannel).where(
                    GuildChannel.guild_id == guild_id,
                    GuildChannel.channel_id == channel_id,
                )
            )
            if gc:
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
            sent = await created.send(
                content,
                username=username,
                avatar_url=avatar,
                files=files,
                wait=True,
                allowed_mentions=ALLOWED_MENTIONS,
                thread=thread,
            )
            webhook = created
        except Exception as e2:  # pragma: no cover - network errors
            if isinstance(e2, discord.HTTPException):
                logging.exception(
                    "webhook.send failed after retry for channel %s: %s %s",
                    channel_id,
                    e2.status,
                    e2.text,
                )
                errors.append(
                    f"Webhook retry failed: {e2.status} {e2.text or _discord_error(e2)}"
                )
            else:
                logging.exception(
                    "webhook.send failed after retry for channel %s", channel_id
                )
                errors.append(f"Webhook retry failed: {e2}")
            return None, None, errors

    discord_msg_id = getattr(sent, "id", None)
    attachments: list[AttachmentDto] | None = None
    if discord_msg_id is None:
        logging.warning(
            "webhook.send returned no id for channel %s", channel_id
        )
        errors.append(f"webhook.send returned no id for channel {channel_id}")
        return None, None, errors
    if sent.attachments:
        attachments = [
            AttachmentDto(
                url=a.url,
                filename=a.filename,
                contentType=a.content_type,
            )
            for a in sent.attachments
        ]

    return discord_msg_id, attachments, errors


class PostBody(BaseModel):
    channel_id: str = Field(alias="channelId")
    content: str
    use_character_name: bool | None = Field(default=False, alias="useCharacterName")
    message_reference: MessageReferenceDto | None = Field(
        default=None, alias="messageReference"
    )


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
    stmt = select(Message).where(
        Message.channel_id == int(channel_id),
        Message.is_officer.is_(is_officer),
    )
    if before is not None:
        stmt = stmt.where(Message.discord_message_id < int(before))
    if after is not None:
        stmt = stmt.where(Message.discord_message_id > int(after))
    stmt = stmt.order_by(Message.created_at.desc())
    if limit is not None:
        stmt = stmt.limit(limit)
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
    channel_id = int(body.channel_id)
    gc_kind = await db.scalar(
        select(GuildChannel.kind).where(
            GuildChannel.guild_id == ctx.guild.id,
            GuildChannel.channel_id == channel_id,
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
    if len(body.content) > 2000:
        raise HTTPException(
            status_code=400, detail="Message too long (max 2000 characters).",
        )
    discord_msg_id: int | None = None
    attachments: list[AttachmentDto] | None = None
    channel = None
    thread_obj: discord.Thread | None = None
    base_channel: discord.abc.Messageable | None = None
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
        channel = discord_client.get_channel(channel_id)
        if channel is None:
            try:
                channel = await discord_client.fetch_channel(channel_id)
            except Exception:
                pass
    if channel is not None:
        if isinstance(channel, discord.Thread):
            thread_obj = channel
            base_channel = getattr(channel, "parent", None)
            if base_channel is None:
                raise HTTPException(status_code=400, detail="parent channel not found")
        else:
            base_channel = channel

        if isinstance(base_channel, discord.CategoryChannel):
            raise HTTPException(status_code=400, detail="cannot post to a category")
        if not isinstance(base_channel, discord.TextChannel):
            raise HTTPException(status_code=400, detail="unsupported channel type")
    discord_files = None
    if files:
        if len(files) > MAX_ATTACHMENTS:
            raise HTTPException(status_code=400, detail="Too many attachments")
        discord_files = []
        for f in files:
            data = await f.read()
            if len(data) > MAX_ATTACHMENT_SIZE:
                raise HTTPException(status_code=400, detail=f"{f.filename} too large")
            discord_files.append(discord.File(io.BytesIO(data), filename=f.filename))

    username_base = nickname or (
        ctx.user.global_name or ("Officer" if is_officer else "Player")
    )
    username = f"{username_base}@FFXIV FC"
    if body.use_character_name and ctx.user.character_name:
        username = f"{username_base} / {ctx.user.character_name}@FFXIV FC"
    username = username[:80]

    discord_msg_id, attachments, webhook_errors = await _send_via_webhook(
        channel=base_channel,
        channel_id=getattr(base_channel, "id", channel_id),
        guild_id=ctx.guild.id,
        content=body.content,
        username=username,
        avatar=avatar,
        files=discord_files,
        db=db,
        thread=thread_obj,
    )
    error_details.extend(webhook_errors)

    if discord_msg_id is None:
        target_channel = thread_obj or base_channel
        if target_channel and isinstance(target_channel, discord.abc.Messageable):
            for f in discord_files or []:
                try:
                    f.reset()
                except Exception:
                    pass
            try:
                sent = await target_channel.send(
                    body.content,
                    files=discord_files,
                    allowed_mentions=ALLOWED_MENTIONS,
                )
                discord_msg_id = getattr(sent, "id", None)
                if discord_msg_id is None:
                    logging.warning(
                        "channel.send returned no id for channel %s", channel_id
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
            except Exception as e:
                if isinstance(e, discord.HTTPException):
                    logging.exception(
                        "channel.send failed for channel %s: %s %s",
                        channel_id,
                        e.status,
                        e.text,
                    )
                    error_details.append(
                        f"Direct send failed: {e.status} {e.text or _discord_error(e)}"
                    )
                else:
                    logging.exception(
                        "channel.send failed for channel %s", channel_id
                    )
                    error_details.append(f"Direct send failed: {e}")
        else:
            logging.warning(
                "Channel %s not found or not messageable", channel_id
            )
            raise HTTPException(status_code=404, detail="channel not found")

    if discord_msg_id is None:
        logging.warning(
            "Failed to relay message to Discord for channel %s", channel_id
        )
        detail: dict[str, object] = {"message": "Failed to relay message to Discord"}
        if error_details:
            detail["discord"] = error_details
        raise HTTPException(status_code=502, detail=detail)
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
        channel=types.SimpleNamespace(id=channel_id),
        author=types.SimpleNamespace(
            id=ctx.user.id,
            display_name=display_name,
            name=display_name,
            display_avatar=None,
        ),
        content=body.content,
        attachments=[
            types.SimpleNamespace(
                url=a.url, filename=a.filename, content_type=a.contentType
            )
            for a in (attachments or [])
        ],
        mentions=[],
        embeds=[],
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
    dto.use_character_name = body.use_character_name
    fragments["author_json"] = author.model_dump_json(by_alias=True, exclude_none=True)

    msg = Message(
        discord_message_id=discord_msg_id,
        channel_id=channel_id,
        guild_id=ctx.guild.id,
        author_id=ctx.user.id,
        author_name=author.name,
        author_avatar_url=author.avatar_url,
        content_raw=body.content,
        content_display=body.content,
        content=body.content,
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

    await manager.broadcast_text(
        dto.model_dump_json(by_alias=True, exclude_none=True),
        ctx.guild.id,
        officer_only=is_officer,
        path="/ws/officer-messages" if is_officer else "/ws/messages",
    )
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
    if len(content) > 2000:
        raise HTTPException(
            status_code=400, detail="Message too long (max 2000 characters)."
        )
    msg = await db.get(Message, int(message_id))
    if not msg or msg.channel_id != int(channel_id) or msg.is_officer != is_officer:
        raise HTTPException(status_code=404)
    if msg.author_id != ctx.user.id:
        raise HTTPException(status_code=403)
    msg.content_raw = msg.content_display = msg.content = content
    msg.edited_timestamp = datetime.utcnow()
    await db.commit()
    if discord_client:
        channel = discord_client.get_channel(int(channel_id))
        if channel and isinstance(channel, discord.abc.Messageable):
            try:
                discord_msg = await channel.fetch_message(int(message_id))
                await discord_msg.edit(content=content)
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
        channel_id=str(channel_id),
        author_name=msg.author_name,
        author_avatar_url=msg.author_avatar_url,
        timestamp=msg.created_at,
        content=content,
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
    await manager.broadcast_text(
        dto.model_dump_json(by_alias=True, exclude_none=True),
        ctx.guild.id,
        officer_only=is_officer,
        path="/ws/officer-messages" if is_officer else "/ws/messages",
    )
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
    if not msg or msg.channel_id != int(channel_id) or msg.is_officer != is_officer:
        raise HTTPException(status_code=404)
    if msg.author_id != ctx.user.id:
        raise HTTPException(status_code=403)
    await db.delete(msg)
    await db.commit()
    if discord_client:
        channel = discord_client.get_channel(int(channel_id))
        if channel and isinstance(channel, discord.abc.Messageable):
            try:
                discord_msg = await channel.fetch_message(int(message_id))
                await discord_msg.delete()
            except Exception:
                pass
    await manager.broadcast_text(
        json.dumps({"id": str(message_id), "channelId": str(channel_id), "deleted": True}),
        ctx.guild.id,
        officer_only=is_officer,
        path="/ws/officer-messages" if is_officer else "/ws/messages",
    )
    return {"ok": True}
