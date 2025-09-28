"""0052_add_syncshell_members_presence_tables

Revision ID: 0052_add_syncshell_members_presence_tables
Revises: 0051_add_syncshell_invites_target_status_index
Create Date: 2024-07-02 00:00:00.000000
"""

from __future__ import annotations

from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import mysql


# revision identifiers, used by Alembic.
revision: str = "0052_add_syncshell_members_presence_tables"
down_revision: Union[str, None] = "0051_add_syncshell_invites_target_status_index"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        "syncshell_members",
        sa.Column("id", sa.Integer(), primary_key=True, autoincrement=True),
        sa.Column("user_id", mysql.BIGINT(unsigned=True), nullable=False),
        sa.Column("member_user_id", mysql.BIGINT(unsigned=True), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.ForeignKeyConstraint(["user_id"], ["users.id"]),
        sa.ForeignKeyConstraint(["member_user_id"], ["users.id"]),
        sa.UniqueConstraint("user_id", "member_user_id", name="uq_syncshell_member_pair"),
    )
    op.create_index("ix_syncshell_members_user_id", "syncshell_members", ["user_id"])
    op.create_index(
        "ix_syncshell_members_member_user_id",
        "syncshell_members",
        ["member_user_id"],
    )

    op.create_table(
        "syncshell_presence",
        sa.Column("id", sa.Integer(), primary_key=True, autoincrement=True),
        sa.Column("user_id", mysql.BIGINT(unsigned=True), nullable=False),
        sa.Column("member_user_id", mysql.BIGINT(unsigned=True), nullable=False),
        sa.Column("active", sa.Boolean(), nullable=False, server_default=sa.text("0")),
        sa.Column("last_seen", sa.DateTime(), nullable=False),
        sa.ForeignKeyConstraint(["user_id"], ["users.id"]),
        sa.ForeignKeyConstraint(["member_user_id"], ["users.id"]),
        sa.UniqueConstraint("user_id", "member_user_id", name="uq_syncshell_presence_pair"),
    )
    op.create_index("ix_syncshell_presence_user_id", "syncshell_presence", ["user_id"])
    op.create_index(
        "ix_syncshell_presence_member_user_id",
        "syncshell_presence",
        ["member_user_id"],
    )


def downgrade() -> None:
    op.drop_index("ix_syncshell_presence_member_user_id", table_name="syncshell_presence")
    op.drop_index("ix_syncshell_presence_user_id", table_name="syncshell_presence")
    op.drop_table("syncshell_presence")

    op.drop_index("ix_syncshell_members_member_user_id", table_name="syncshell_members")
    op.drop_index("ix_syncshell_members_user_id", table_name="syncshell_members")
    op.drop_table("syncshell_members")
