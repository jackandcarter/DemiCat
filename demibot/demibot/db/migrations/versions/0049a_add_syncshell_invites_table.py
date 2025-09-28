"""0049a_add_syncshell_invites_table

Revision ID: 0049a_add_syncshell_invites_table
Revises: 0049_add_installation_asset_hash
Create Date: 2024-06-08 00:00:00.000000
"""

from __future__ import annotations

from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects.mysql import BIGINT


# revision identifiers, used by Alembic.
revision: str = "0049a_add_syncshell_invites_table"
down_revision: Union[str, None] = "0049_add_installation_asset_hash"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        "syncshell_invites",
        sa.Column("id", sa.String(length=64), primary_key=True),
        sa.Column(
            "inviter_id",
            BIGINT(unsigned=True),
            sa.ForeignKey("users.id"),
            nullable=False,
        ),
        sa.Column(
            "target_user_id",
            BIGINT(unsigned=True),
            sa.ForeignKey("users.id"),
            nullable=True,
        ),
        sa.Column("target_display_name", sa.String(length=255), nullable=False),
        sa.Column(
            "status",
            sa.String(length=32),
            nullable=False,
            server_default=sa.text("'pending'"),
        ),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
    )
    op.create_index(
        "ix_syncshell_invites_inviter_id",
        "syncshell_invites",
        ["inviter_id"],
    )
    op.create_index(
        "ix_syncshell_invites_target_user_id",
        "syncshell_invites",
        ["target_user_id"],
    )
    op.create_index(
        "ix_syncshell_invites_status",
        "syncshell_invites",
        ["status"],
    )


def downgrade() -> None:
    op.drop_index("ix_syncshell_invites_status", table_name="syncshell_invites")
    op.drop_index(
        "ix_syncshell_invites_target_user_id",
        table_name="syncshell_invites",
    )
    op.drop_index("ix_syncshell_invites_inviter_id", table_name="syncshell_invites")
    op.drop_table("syncshell_invites")
