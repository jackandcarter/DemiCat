from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, List


@dataclass
class Presence:
    id: int
    name: str
    status: str
    avatar_url: str | None = None


_presences: Dict[int, Dict[int, Presence]] = {}


def set_presence(guild_id: int, presence: Presence) -> None:
    guild = _presences.setdefault(guild_id, {})
    existing = guild.get(presence.id)
    if existing and presence.avatar_url is None:
        presence.avatar_url = existing.avatar_url
    guild[presence.id] = presence


def get_presences(guild_id: int) -> List[Presence]:
    return list(_presences.get(guild_id, {}).values())
