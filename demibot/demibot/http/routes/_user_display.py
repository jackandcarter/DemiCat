from __future__ import annotations

from ..deps import RequestContext

FOOTER_TEXT_LIMIT = 2048


def compute_creator_base_name(ctx: RequestContext, nickname: str | None) -> str:
    user = getattr(ctx, "user", None)
    global_name = getattr(user, "global_name", None)
    roles = getattr(ctx, "roles", None) or []
    is_officer = False
    try:
        is_officer = "officer" in roles
    except TypeError:
        is_officer = False

    name = nickname or global_name or ("Officer" if is_officer else "Player")
    if name:
        name = name.strip()
    if not name:
        name = "Player"
    return name


def build_creator_label(base_name: str) -> str:
    label = f"Event created by {base_name}"
    if len(label) > FOOTER_TEXT_LIMIT:
        label = label[:FOOTER_TEXT_LIMIT]
    return label
