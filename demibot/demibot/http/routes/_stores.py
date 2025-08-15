
from __future__ import annotations
from collections import defaultdict
from typing import Dict, List
from dataclasses import dataclass, field
from datetime import datetime
import uuid

@dataclass
class User:
    id: str
    name: str

@dataclass
class Message:
    id: str
    channel_id: str
    author_name: str
    content: str
    mentions: list[User] | None = None
    is_officer: bool = False

@dataclass
class Embed:
    id: str
    payload: dict  # already shaped as EmbedDto

# Global stores
USERS: list[User] = [
    User(id="1001", name="Alice"),
    User(id="1002", name="Bob"),
    User(id="1003", name="Charlie"),
]

CHANNELS = {
    "event": ["events-1", "events-2"],
    "fc_chat": ["fc-general"],
    "officer_chat": ["officer-room"],
    "officer_visible": ["officer-room"],
}

MESSAGES: Dict[str, List[Message]] = defaultdict(list)          # FC chat
OFFICER_MESSAGES: Dict[str, List[Message]] = defaultdict(list)  # Officer chat

EMBEDS: Dict[str, Embed] = {}  # keyed by embed id
