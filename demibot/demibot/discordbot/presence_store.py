from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List


@dataclass
class Presence:
    id: int
    name: str
    status: str
    avatar_url: str | None = None
    roles: list[int] = field(default_factory=list)
    status_text: str | None = None


_presences: Dict[int, Dict[int, Presence]] = {}


def set_presence(guild_id: int, presence: Presence) -> None:
    guild = _presences.setdefault(guild_id, {})
    existing = guild.get(presence.id)
    if existing:
        if presence.avatar_url is None:
            presence.avatar_url = existing.avatar_url
        if presence.status_text is None:
            presence.status_text = existing.status_text
    guild[presence.id] = presence


def get_presences(guild_id: int) -> List[Presence]:
    return list(_presences.get(guild_id, {}).values())
