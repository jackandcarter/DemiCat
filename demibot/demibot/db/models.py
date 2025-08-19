from __future__ import annotations

from datetime import datetime
from typing import Optional

from sqlalchemy import (
    BigInteger,
    Boolean,
    DateTime,
    ForeignKey,
    Integer,
    String,
    Text,
    Index,
)
from sqlalchemy.dialects.mysql import BIGINT
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
    officer_visible_channel_id: Mapped[Optional[int]] = mapped_column(BigInteger)
    officer_role_id: Mapped[Optional[int]] = mapped_column(BigInteger)

    guild: Mapped[Guild] = relationship(back_populates="config")


class GuildChannel(Base):
    __tablename__ = "guild_channels"

    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"), primary_key=True)
    channel_id: Mapped[int] = mapped_column(BigInteger, primary_key=True)
    kind: Mapped[str] = mapped_column(String(24), primary_key=True)
    name: Mapped[Optional[str]] = mapped_column(String(255))


class User(Base):
    __tablename__ = "users"

    id: Mapped[int] = mapped_column(BIGINT(unsigned=True), primary_key=True)
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
    user_id: Mapped[int] = mapped_column(BIGINT(unsigned=True), ForeignKey("users.id"))
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"))
    token: Mapped[str] = mapped_column(String(64), unique=True, index=True)
    enabled: Mapped[bool] = mapped_column(Boolean, default=True)
    roles_cached: Mapped[Optional[str]] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )
    last_used_at: Mapped[Optional[datetime]] = mapped_column(DateTime)


class Membership(Base):
    __tablename__ = "memberships"

    id: Mapped[int] = mapped_column(primary_key=True)
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"))
    user_id: Mapped[int] = mapped_column(BIGINT(unsigned=True), ForeignKey("users.id"))


class Role(Base):
    __tablename__ = "roles"

    id: Mapped[int] = mapped_column(primary_key=True)
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"))
    discord_role_id: Mapped[int] = mapped_column(BigInteger, unique=True)
    name: Mapped[str] = mapped_column(String(255))
    is_officer: Mapped[bool] = mapped_column(Boolean, default=False)
    is_chat: Mapped[bool] = mapped_column(Boolean, default=False)


class MembershipRole(Base):
    __tablename__ = "membership_roles"

    membership_id: Mapped[int] = mapped_column(
        ForeignKey("memberships.id"), primary_key=True
    )
    role_id: Mapped[int] = mapped_column(ForeignKey("roles.id"), primary_key=True)


class Message(Base):
    __tablename__ = "messages"
    __table_args__ = (
        Index("ix_messages_channel_id_created_at", "channel_id", "created_at"),
    )

    discord_message_id: Mapped[int] = mapped_column(BigInteger, primary_key=True)
    channel_id: Mapped[int] = mapped_column(BigInteger, index=True)
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"))
    author_id: Mapped[int] = mapped_column(
        BIGINT(unsigned=True), ForeignKey("users.id")
    )
    author_name: Mapped[str] = mapped_column(String(255))
    content_raw: Mapped[str] = mapped_column(Text)
    content_display: Mapped[str] = mapped_column(Text)
    is_officer: Mapped[bool] = mapped_column(Boolean, default=False)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)


class Embed(Base):
    __tablename__ = "embeds"

    discord_message_id: Mapped[int] = mapped_column(
        BigInteger, primary_key=True, index=True
    )
    channel_id: Mapped[int] = mapped_column(BigInteger, index=True)
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"))
    payload_json: Mapped[str] = mapped_column(Text)
    source: Mapped[str] = mapped_column(String(16))
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )


class Attendance(Base):
    __tablename__ = "attendance"

    discord_message_id: Mapped[int] = mapped_column(BigInteger, primary_key=True)
    user_id: Mapped[int] = mapped_column(
        BIGINT(unsigned=True), ForeignKey("users.id"), primary_key=True
    )
    choice: Mapped[str] = mapped_column(String(50))
