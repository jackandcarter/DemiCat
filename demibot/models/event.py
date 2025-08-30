from __future__ import annotations

from datetime import datetime
from typing import Dict, List, Optional

from sqlalchemy import BigInteger, DateTime, ForeignKey, JSON
from sqlalchemy.orm import Mapped, mapped_column

from demibot.db.base import Base


class Event(Base):
    """Database model for stored Discord events.

    The table keeps track of the originating message along with any embeds or
    attachments captured from the original Discord post. The ``embeds`` and
    ``attachments`` columns use SQLAlchemy's ``JSON`` type so they can store
    arbitrary structured data without requiring a schema migration each time
    Discord tweaks its payload format.
    """

    __tablename__ = "events"

    discord_message_id: Mapped[int] = mapped_column(BigInteger, primary_key=True)
    channel_id: Mapped[int] = mapped_column(BigInteger, index=True)
    guild_id: Mapped[int] = mapped_column(ForeignKey("guilds.id"))

    # New JSON columns capturing the full embed and attachment payloads
    embeds: Mapped[Optional[List[Dict]]] = mapped_column(JSON, nullable=True)
    attachments: Mapped[Optional[List[Dict]]] = mapped_column(JSON, nullable=True)

    created_at: Mapped[datetime] = mapped_column(DateTime, default=datetime.utcnow)
