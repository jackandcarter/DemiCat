"""0058_drop_syncshell_tables

Revision ID: 0058_drop_syncshell_tables
Revises: 0057_expand_presence_status_text_to_text
Create Date: 2025-10-20 00:00:00.000000
"""

from __future__ import annotations

from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects.mysql import BIGINT

# revision identifiers, used by Alembic.
revision: str = "0058_drop_syncshell_tables"
down_revision: Union[str, None] = "0057_expand_presence_status_text_to_text"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    # Drop syncshell presence tracking tables
    op.drop_index("ix_syncshell_presence_member_user_id", table_name="syncshell_presence")
    op.drop_index("ix_syncshell_presence_user_id", table_name="syncshell_presence")
    op.drop_table("syncshell_presence")

    op.drop_index("ix_syncshell_members_member_user_id", table_name="syncshell_members")
    op.drop_index("ix_syncshell_members_user_id", table_name="syncshell_members")
    op.drop_table("syncshell_members")

    # Drop invite tables and related indexes
    op.drop_index("ix_syncshell_invites_target_status", table_name="syncshell_invites")
    op.drop_index(
        "ix_syncshell_invites_inviter_target_display_name_status",
        table_name="syncshell_invites",
    )
    op.drop_index(
        "ix_syncshell_invites_inviter_target_user_status",
        table_name="syncshell_invites",
    )
    op.drop_index("ix_syncshell_invites_status", table_name="syncshell_invites")
    op.drop_index("ix_syncshell_invites_target_user_id", table_name="syncshell_invites")
    op.drop_index("ix_syncshell_invites_inviter_id", table_name="syncshell_invites")
    op.drop_table("syncshell_invites")

    # Drop transfer budget tracking
    op.drop_table("syncshell_transfer_budgets")

    # Drop pairing metadata and rate limits
    op.drop_table("syncshell_rate_limits")
    op.drop_index("ix_syncshell_pairings_expires_at", table_name="syncshell_pairings")
    op.drop_index("ix_syncshell_pairings_token", table_name="syncshell_pairings")
    op.drop_table("syncshell_pairings")
    op.drop_table("syncshell_manifests")


def downgrade() -> None:
    # Recreate pairing manifests and rate limit structures
    op.create_table(
        "syncshell_manifests",
        sa.Column("user_id", BIGINT(unsigned=True), sa.ForeignKey("users.id"), primary_key=True),
        sa.Column("manifest_json", sa.Text(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
    )

    op.create_table(
        "syncshell_pairings",
        sa.Column("user_id", BIGINT(unsigned=True), sa.ForeignKey("users.id"), primary_key=True),
        sa.Column("token", sa.String(length=64), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("expires_at", sa.DateTime(), nullable=False),
    )
    op.create_index(
        "ix_syncshell_pairings_token",
        "syncshell_pairings",
        ["token"],
        unique=True,
    )
    op.create_index(
        "ix_syncshell_pairings_expires_at",
        "syncshell_pairings",
        ["expires_at"],
    )
    op.create_table(
        "syncshell_rate_limits",
        sa.Column("user_id", BIGINT(unsigned=True), sa.ForeignKey("users.id"), primary_key=True),
        sa.Column("requests", sa.Integer(), nullable=False, server_default="0"),
        sa.Column("window_start", sa.DateTime(), nullable=False),
    )

    # Recreate transfer budget tracking
    op.create_table(
        "syncshell_transfer_budgets",
        sa.Column("user_id", BIGINT(unsigned=True), sa.ForeignKey("users.id"), primary_key=True),
        sa.Column("window_start", sa.DateTime(), nullable=False),
        sa.Column("used_bytes", BIGINT(unsigned=True), nullable=False, server_default="0"),
    )

    # Recreate invite tables and indexes
    op.create_table(
        "syncshell_invites",
        sa.Column("id", sa.String(length=64), primary_key=True),
        sa.Column("inviter_id", BIGINT(unsigned=True), sa.ForeignKey("users.id"), nullable=False),
        sa.Column("target_user_id", BIGINT(unsigned=True), sa.ForeignKey("users.id"), nullable=True),
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
    op.create_index("ix_syncshell_invites_inviter_id", "syncshell_invites", ["inviter_id"])
    op.create_index("ix_syncshell_invites_target_user_id", "syncshell_invites", ["target_user_id"])
    op.create_index("ix_syncshell_invites_status", "syncshell_invites", ["status"])
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
    op.create_index(
        "ix_syncshell_invites_target_status",
        "syncshell_invites",
        ["target_user_id", "status"],
    )

    # Recreate membership and presence tracking
    op.create_table(
        "syncshell_members",
        sa.Column("id", sa.Integer(), primary_key=True, autoincrement=True),
        sa.Column("user_id", BIGINT(unsigned=True), nullable=False),
        sa.Column("member_user_id", BIGINT(unsigned=True), nullable=False),
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
        sa.Column("user_id", BIGINT(unsigned=True), nullable=False),
        sa.Column("member_user_id", BIGINT(unsigned=True), nullable=False),
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

