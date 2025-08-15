from __future__ import annotations

import enum
from datetime import datetime
from typing import List, Optional

from sqlalchemy import BigInteger, Boolean, DateTime, ForeignKey, Integer, String, Text
from sqlalchemy.orm import Mapped, mapped_column, relationship

from .base import Base


class Guild(Base):
    __tablename__ = "guilds"

    id: Mapped[int] = mapped_column(primary_key=True)
    discord_guild_id: Mapped[int] = mapped_column(BigInteger, unique=True, index=True)
    name: Mapped[str] = mapped_column(String(255))
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )

    config: Mapped["GuildConfig"] = relationship(back_populates="guild", uselist=False)


class GuildConfig(Base):
    __tablename__ = "guild_config"

    id: Mapped[int] = mapped_column(primary_key=True)
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"))
    event_channel_id: Mapped[Optional[int]] = mapped_column(BigInteger)
    fc_chat_channel_id: Mapped[Optional[int]] = mapped_column(BigInteger)
    officer_chat_channel_id: Mapped[Optional[int]] = mapped_column(BigInteger)
    officer_visible_channel_id: Mapped[Optional[int]] = mapped_column(BigInteger)
    officer_role_id: Mapped[Optional[int]] = mapped_column(BigInteger)
    chat_role_id: Mapped[Optional[int]] = mapped_column(BigInteger)

    guild: Mapped[Guild] = relationship(back_populates="config")


class User(Base):
    __tablename__ = "users"

    id: Mapped[int] = mapped_column(primary_key=True)
    discord_user_id: Mapped[int] = mapped_column(BigInteger, unique=True, index=True)
    global_name: Mapped[Optional[str]] = mapped_column(String(255))
    discriminator: Mapped[Optional[str]] = mapped_column(String(10))
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )


class UserKey(Base):
    __tablename__ = "user_keys"

    id: Mapped[int] = mapped_column(primary_key=True)
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id"))
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"))
    token: Mapped[str] = mapped_column(String(64), unique=True, index=True)
    enabled: Mapped[bool] = mapped_column(Boolean, default=True)
    roles_cached: Mapped[Optional[str]] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )
    last_used_at: Mapped[Optional[datetime]] = mapped_column(DateTime)


class Message(Base):
    __tablename__ = "messages"

    id: Mapped[int] = mapped_column(primary_key=True)
    discord_message_id: Mapped[int] = mapped_column(BigInteger, index=True)
    channel_id: Mapped[int] = mapped_column(BigInteger, index=True)
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"))
    author_id: Mapped[int] = mapped_column(ForeignKey("users.id"))
    author_name: Mapped[str] = mapped_column(String(255))
    content_raw: Mapped[str] = mapped_column(Text)
    content_display: Mapped[str] = mapped_column(Text)
    mentions_json: Mapped[Optional[str]] = mapped_column(Text)
    is_officer: Mapped[bool] = mapped_column(Boolean, default=False)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)


class Embed(Base):
    __tablename__ = "embeds"

    id: Mapped[int] = mapped_column(primary_key=True)
    discord_message_id: Mapped[int] = mapped_column(BigInteger, index=True)
    channel_id: Mapped[int] = mapped_column(BigInteger, index=True)
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"))
    payload_json: Mapped[str] = mapped_column(Text)
    last_broadcast_at: Mapped[Optional[datetime]] = mapped_column(DateTime)


class RSVP(enum.Enum):
    yes = "yes"
    maybe = "maybe"
    no = "no"


class Attendance(Base):
    __tablename__ = "attendance"

    id: Mapped[int] = mapped_column(primary_key=True)
    discord_message_id: Mapped[int] = mapped_column(BigInteger, index=True)
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id"))
    choice: Mapped[RSVP] = mapped_column(String(10))
    updated_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
