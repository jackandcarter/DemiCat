"""0058_drop_syncshell_tables

Revision ID: 0058_drop_syncshell_tables
Revises: 0057_expand_presence_status_text_to_text
Create Date: 2025-10-20 00:00:00.000000
"""

from __future__ import annotations

from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0058_drop_syncshell_tables"
down_revision: Union[str, None] = "0057_expand_presence_status_text_to_text"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def _table_exists(table_name: str) -> bool:
    inspector = sa.inspect(op.get_bind())
    return inspector.has_table(table_name)


def _drop_index_if_exists(index_name: str, table_name: str) -> None:
    inspector = sa.inspect(op.get_bind())
    if not inspector.has_table(table_name):
        return

    indexes = {index["name"] for index in inspector.get_indexes(table_name)}
    if index_name in indexes:
        op.drop_index(index_name, table_name=table_name)


def _drop_table_if_exists(table_name: str) -> None:
    if _table_exists(table_name):
        op.drop_table(table_name)


def upgrade() -> None:
    """Remove legacy SyncShell tables when present."""

    # Drop syncshell presence tracking tables
    _drop_index_if_exists("ix_syncshell_presence_member_user_id", "syncshell_presence")
    _drop_index_if_exists("ix_syncshell_presence_user_id", "syncshell_presence")
    _drop_table_if_exists("syncshell_presence")

    _drop_index_if_exists("ix_syncshell_members_member_user_id", "syncshell_members")
    _drop_index_if_exists("ix_syncshell_members_user_id", "syncshell_members")
    _drop_table_if_exists("syncshell_members")

    # Drop invite tables and related indexes
    _drop_index_if_exists("ix_syncshell_invites_target_status", "syncshell_invites")
    _drop_index_if_exists(
        "ix_syncshell_invites_inviter_target_display_name_status",
        "syncshell_invites",
    )
    _drop_index_if_exists(
        "ix_syncshell_invites_inviter_target_user_status",
        "syncshell_invites",
    )
    _drop_index_if_exists("ix_syncshell_invites_status", "syncshell_invites")
    _drop_index_if_exists("ix_syncshell_invites_target_user_id", "syncshell_invites")
    _drop_index_if_exists("ix_syncshell_invites_inviter_id", "syncshell_invites")
    _drop_table_if_exists("syncshell_invites")

    # Drop transfer budget tracking
    _drop_table_if_exists("syncshell_transfer_budgets")

    # Drop pairing metadata and rate limits
    _drop_table_if_exists("syncshell_rate_limits")
    _drop_index_if_exists("ix_syncshell_pairings_expires_at", "syncshell_pairings")
    _drop_index_if_exists("ix_syncshell_pairings_token", "syncshell_pairings")
    _drop_table_if_exists("syncshell_pairings")
    _drop_table_if_exists("syncshell_manifests")


def downgrade() -> None:
    """Downgrade is a no-op because SyncShell support was removed entirely."""

    # SyncShell is deprecated; keep schema unchanged on downgrade.
    pass

