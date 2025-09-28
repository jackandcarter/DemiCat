"""0051_add_syncshell_invites_target_status_index

Revision ID: 0051_add_syncshell_invites_target_status_index
Revises: 0050_add_syncshell_invite_indexes
Create Date: 2024-06-09 00:00:00.000000
"""

from __future__ import annotations

from typing import Sequence, Union

from alembic import op


# revision identifiers, used by Alembic.
revision: str = "0051_add_syncshell_invites_target_status_index"
down_revision: Union[str, None] = "0050_add_syncshell_invite_indexes"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_index(
        "ix_syncshell_invites_target_status",
        "syncshell_invites",
        ["target_user_id", "status"],
    )


def downgrade() -> None:
    op.drop_index(
        "ix_syncshell_invites_target_status",
        table_name="syncshell_invites",
    )
