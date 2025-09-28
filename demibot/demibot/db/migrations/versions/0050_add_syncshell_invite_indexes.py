"""0050_add_syncshell_invite_indexes

Revision ID: 0050_add_syncshell_invite_indexes
Revises: 0049_add_installation_asset_hash
Create Date: 2024-06-09 00:00:00.000000
"""

from __future__ import annotations

from typing import Sequence, Union

from alembic import op


# revision identifiers, used by Alembic.
revision: str = "0050_add_syncshell_invite_indexes"
down_revision: Union[str, None] = "0049_add_installation_asset_hash"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_index(
        "ix_syncshell_invites_inviter_target_user_status",
        "syncshell_invites",
        ["inviter_id", "target_user_id", "status"],
    )
    op.create_index(
        "ix_syncshell_invites_inviter_target_display_name_status",
        "syncshell_invites",
        ["inviter_id", "target_display_name", "status"],
    )


def downgrade() -> None:
    op.drop_index(
        "ix_syncshell_invites_inviter_target_display_name_status",
        table_name="syncshell_invites",
    )
    op.drop_index(
        "ix_syncshell_invites_inviter_target_user_status",
        table_name="syncshell_invites",
    )
