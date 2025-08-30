"""add syncshell pairing and manifest tables

Revision ID: 0029_add_syncshell_tables
Revises: 0028_add_guild_channel_unique
Create Date: 2025-01-01 00:00:00.000000
"""

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects.mysql import BIGINT

# revision identifiers, used by Alembic.
revision = "0029_add_syncshell_tables"
down_revision = "0028_add_guild_channel_unique"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "syncshell_pairings",
        sa.Column("user_id", BIGINT(unsigned=True), sa.ForeignKey("users.id"), primary_key=True),
        sa.Column("token", sa.String(length=64), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
    )
    op.create_index(
        "ix_syncshell_pairings_token",
        "syncshell_pairings",
        ["token"],
        unique=True,
    )
    op.create_table(
        "syncshell_manifests",
        sa.Column("user_id", BIGINT(unsigned=True), sa.ForeignKey("users.id"), primary_key=True),
        sa.Column("manifest_json", sa.Text(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
    )


def downgrade() -> None:
    op.drop_table("syncshell_manifests")
    op.drop_index("ix_syncshell_pairings_token", table_name="syncshell_pairings")
    op.drop_table("syncshell_pairings")
