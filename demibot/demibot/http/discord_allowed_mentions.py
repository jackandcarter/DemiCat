from __future__ import annotations

import discord


try:  # pragma: no cover - optional dependency in tests
    ALLOWED_MENTIONS = discord.AllowedMentions(users=True, roles=True, everyone=False)
except AttributeError:  # pragma: no cover - fallback for stub discord module
    class _AllowedMentionsFallback:
        users = True
        roles = True
        everyone = False

        def to_dict(self) -> dict[str, object]:
            return {"users": [], "roles": [], "everyone": False}

    ALLOWED_MENTIONS = _AllowedMentionsFallback()


__all__ = ["ALLOWED_MENTIONS"]
