from __future__ import annotations

from datetime import datetime
from enum import Enum
from typing import Optional

from sqlalchemy import (
    BigInteger,
    Boolean,
    DateTime,
    Enum as SAEnum,
    ForeignKey,
    Integer,
    String,
    Text,
    Index,
)
from sqlalchemy.dialects.mysql import BIGINT
from sqlalchemy.orm import Mapped, mapped_column, relationship

from .base import Base


class RequestType(str, Enum):
    ITEM = "item"
    RUN = "run"
    EVENT = "event"


class RequestStatus(str, Enum):
    OPEN = "open"
    CLAIMED = "claimed"
    IN_PROGRESS = "in_progress"
    AWAITING_CONFIRM = "awaiting_confirm"
    COMPLETED = "completed"
    CANCELLED = "cancelled"
    APPROVED = "approved"  # legacy
    DENIED = "denied"  # legacy


class Urgency(str, Enum):
    LOW = "low"
    MEDIUM = "medium"
    HIGH = "high"


class AssetKind(str, Enum):
    APPEARANCE = "appearance"
    FILE = "file"
    SCRIPT = "script"


class InstallStatus(str, Enum):
    PENDING = "pending"
    INSTALLED = "installed"
    FAILED = "failed"


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

    installations: Mapped[list["UserInstallation"]] = relationship(
        back_populates="user", cascade="all, delete-orphan"
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
    author_avatar_url: Mapped[Optional[str]] = mapped_column(String(255), nullable=True)
    content_raw: Mapped[str] = mapped_column(Text)
    content_display: Mapped[str] = mapped_column(Text)
    attachments_json: Mapped[Optional[str]] = mapped_column(Text, nullable=True)
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
    buttons_json: Mapped[Optional[str]] = mapped_column(Text, nullable=True)
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


class Presence(Base):
    __tablename__ = "presences"
    __table_args__ = (
        Index("ix_presences_guild_id_status", "guild_id", "status"),
    )

    guild_id: Mapped[int] = mapped_column(BigInteger, primary_key=True)
    user_id: Mapped[int] = mapped_column(BigInteger, primary_key=True)
    status: Mapped[str] = mapped_column(String(16))
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )


class RecurringEvent(Base):
    __tablename__ = "recurring_events"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True)
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"))
    channel_id: Mapped[int] = mapped_column(BigInteger)
    repeat: Mapped[str] = mapped_column(String(16))
    next_post_at: Mapped[datetime] = mapped_column(DateTime)
    payload_json: Mapped[str] = mapped_column(Text)


class SignupPreset(Base):
    __tablename__ = "signup_presets"

    id: Mapped[int] = mapped_column(primary_key=True)
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"), index=True)
    name: Mapped[str] = mapped_column(String(255))
    buttons_json: Mapped[str] = mapped_column(Text)


class EventButton(Base):
    __tablename__ = "event_buttons"

    message_id: Mapped[int] = mapped_column(BigInteger, primary_key=True)
    tag: Mapped[str] = mapped_column(String(50), primary_key=True)
    label: Mapped[str] = mapped_column(String(255))
    emoji: Mapped[Optional[str]] = mapped_column(String(64))
    style: Mapped[Optional[int]] = mapped_column(Integer)
    max_signups: Mapped[Optional[int]] = mapped_column(Integer)


class Request(Base):
    __tablename__ = "requests"
    __table_args__ = (
        Index("ix_requests_type", "type"),
        Index("ix_requests_status", "status"),
        Index("ix_requests_urgency", "urgency"),
        Index("ix_requests_text", "title", "description", mysql_prefix="FULLTEXT"),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"))
    user_id: Mapped[int] = mapped_column(
        BIGINT(unsigned=True), ForeignKey("users.id"), index=True
    )
    assignee_id: Mapped[Optional[int]] = mapped_column(
        BIGINT(unsigned=True), ForeignKey("users.id"), nullable=True, index=True
    )
    title: Mapped[str] = mapped_column(String(255))
    description: Mapped[Optional[str]] = mapped_column(Text)
    type: Mapped[RequestType] = mapped_column(SAEnum(RequestType))
    status: Mapped[RequestStatus] = mapped_column(SAEnum(RequestStatus))
    urgency: Mapped[Urgency] = mapped_column(SAEnum(Urgency))
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )
    version: Mapped[int] = mapped_column(Integer, default=1, nullable=False)

    __mapper_args__ = {"version_id_col": version}

    items: Mapped[list["RequestItem"]] = relationship(
        back_populates="request", cascade="all, delete-orphan"
    )
    runs: Mapped[list["RequestRun"]] = relationship(
        back_populates="request", cascade="all, delete-orphan"
    )
    events: Mapped[list["RequestEvent"]] = relationship(
        back_populates="request", cascade="all, delete-orphan"
    )


class RequestItem(Base):
    __tablename__ = "request_items"

    id: Mapped[int] = mapped_column(primary_key=True)
    request_id: Mapped[int] = mapped_column(
        ForeignKey("requests.id", ondelete="CASCADE"), index=True
    )
    item_id: Mapped[int] = mapped_column(BigInteger)
    quantity: Mapped[int] = mapped_column(Integer, default=1)
    hq: Mapped[bool] = mapped_column(Boolean, default=False)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )
    version: Mapped[int] = mapped_column(Integer, default=1, nullable=False)

    __mapper_args__ = {"version_id_col": version}

    request: Mapped[Request] = relationship(back_populates="items")


class RequestRun(Base):
    __tablename__ = "request_runs"

    id: Mapped[int] = mapped_column(primary_key=True)
    request_id: Mapped[int] = mapped_column(
        ForeignKey("requests.id", ondelete="CASCADE"), index=True
    )
    run_id: Mapped[int] = mapped_column(BigInteger)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )
    version: Mapped[int] = mapped_column(Integer, default=1, nullable=False)

    __mapper_args__ = {"version_id_col": version}

    request: Mapped[Request] = relationship(back_populates="runs")


class RequestEvent(Base):
    __tablename__ = "request_events"

    id: Mapped[int] = mapped_column(primary_key=True)
    request_id: Mapped[int] = mapped_column(
        ForeignKey("requests.id", ondelete="CASCADE"), index=True
    )
    event_id: Mapped[int] = mapped_column(BigInteger)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )
    version: Mapped[int] = mapped_column(Integer, default=1, nullable=False)

    __mapper_args__ = {"version_id_col": version}

    request: Mapped[Request] = relationship(back_populates="events")


class Fc(Base):
    __tablename__ = "fc"

    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(255))
    world: Mapped[str] = mapped_column(String(32))
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )

    assets: Mapped[list["Asset"]] = relationship(
        back_populates="fc", cascade="all, delete-orphan"
    )
    bundles: Mapped[list["AppearanceBundle"]] = relationship(
        back_populates="fc", cascade="all, delete-orphan"
    )
    users: Mapped[list["FcUser"]] = relationship(
        back_populates="fc", cascade="all, delete-orphan"
    )


class FcUser(Base):
    __tablename__ = "fc_user"

    fc_id: Mapped[int] = mapped_column(
        ForeignKey("fc.id", ondelete="CASCADE"), primary_key=True
    )
    user_id: Mapped[int] = mapped_column(
        BIGINT(unsigned=True),
        ForeignKey("users.id", ondelete="CASCADE"),
        primary_key=True,
    )
    joined_at: Mapped[datetime] = mapped_column(DateTime)
    last_pull_at: Mapped[Optional[datetime]] = mapped_column(DateTime, nullable=True)
    settings: Mapped[Optional[str]] = mapped_column(Text)
    consent_sync: Mapped[bool] = mapped_column(Boolean, default=False)

    fc: Mapped[Fc] = relationship(back_populates="users")
    user: Mapped[User] = relationship()


class AssetDependency(Base):
    __tablename__ = "asset_dependency"

    asset_id: Mapped[int] = mapped_column(
        ForeignKey("asset.id", ondelete="CASCADE"), primary_key=True
    )
    dependency_id: Mapped[int] = mapped_column(
        ForeignKey("asset.id", ondelete="CASCADE"), primary_key=True
    )


class Asset(Base):
    __tablename__ = "asset"
    __table_args__ = (
        Index("ix_asset_fc_id", "fc_id"),
        Index("ix_asset_kind", "kind"),
        Index("ix_asset_hash", "hash", unique=True),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    fc_id: Mapped[Optional[int]] = mapped_column(
        ForeignKey("fc.id", ondelete="CASCADE"), nullable=True
    )
    kind: Mapped[AssetKind] = mapped_column(SAEnum(AssetKind), nullable=False)
    name: Mapped[str] = mapped_column(String(255))
    hash: Mapped[str] = mapped_column(String(64))
    size: Mapped[Optional[int]] = mapped_column(Integer)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )
    version: Mapped[int] = mapped_column(Integer, default=1, nullable=False)

    __mapper_args__ = {"version_id_col": version}

    fc: Mapped[Optional[Fc]] = relationship(back_populates="assets")
    bundles: Mapped[list["AppearanceBundle"]] = relationship(
        "AppearanceBundle", secondary="appearance_bundle_item", back_populates="assets"
    )
    installations: Mapped[list["UserInstallation"]] = relationship(
        back_populates="asset", cascade="all, delete-orphan"
    )


class AppearanceBundle(Base):
    __tablename__ = "appearance_bundle"

    id: Mapped[int] = mapped_column(primary_key=True)
    fc_id: Mapped[Optional[int]] = mapped_column(
        ForeignKey("fc.id", ondelete="CASCADE"), nullable=True
    )
    name: Mapped[str] = mapped_column(String(255))
    description: Mapped[Optional[str]] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )

    fc: Mapped[Optional[Fc]] = relationship(back_populates="bundles")
    items: Mapped[list["AppearanceBundleItem"]] = relationship(
        back_populates="bundle", cascade="all, delete-orphan"
    )
    assets: Mapped[list[Asset]] = relationship(
        "Asset", secondary="appearance_bundle_item", back_populates="bundles"
    )


class AppearanceBundleItem(Base):
    __tablename__ = "appearance_bundle_item"

    bundle_id: Mapped[int] = mapped_column(
        ForeignKey("appearance_bundle.id", ondelete="CASCADE"), primary_key=True
    )
    asset_id: Mapped[int] = mapped_column(
        ForeignKey("asset.id", ondelete="CASCADE"), primary_key=True
    )
    quantity: Mapped[int] = mapped_column(Integer, default=1, nullable=False)

    bundle: Mapped[AppearanceBundle] = relationship(back_populates="items")
    asset: Mapped[Asset] = relationship()


class UserInstallation(Base):
    __tablename__ = "user_installation"
    __table_args__ = (
        Index("ix_user_installation_user_id", "user_id"),
        Index("ix_user_installation_asset_id", "asset_id"),
        Index("ix_user_installation_status", "status"),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    user_id: Mapped[int] = mapped_column(
        BIGINT(unsigned=True),
        ForeignKey("users.id", ondelete="CASCADE"),
        nullable=False,
    )
    asset_id: Mapped[int] = mapped_column(
        ForeignKey("asset.id", ondelete="CASCADE"), nullable=False
    )
    status: Mapped[InstallStatus] = mapped_column(
        SAEnum(InstallStatus), nullable=False
    )
    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )
    version: Mapped[int] = mapped_column(Integer, default=1, nullable=False)

    __mapper_args__ = {"version_id_col": version}

    user: Mapped[User] = relationship(back_populates="installations")
    asset: Mapped[Asset] = relationship(back_populates="installations")


class IndexCheckpoint(Base):
    __tablename__ = "index_checkpoint"
    __table_args__ = (
        Index("ix_index_checkpoint_kind", "kind", unique=True),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    kind: Mapped[AssetKind] = mapped_column(SAEnum(AssetKind), nullable=False)
    last_id: Mapped[int] = mapped_column(Integer, nullable=False)
    last_generated_at: Mapped[datetime] = mapped_column(
        DateTime, default=datetime.utcnow, onupdate=datetime.utcnow
    )
