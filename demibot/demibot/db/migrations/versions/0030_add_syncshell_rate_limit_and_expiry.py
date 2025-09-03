"""add syncshell rate limit and pairing expiry

Revision ID: 0030_add_syncshell_rate_limit_and_expiry
Revises: 0029_add_syncshell_tables
Create Date: 2025-01-01 00:00:00.000000
"""

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects.mysql import BIGINT

# revision identifiers, used by Alembic.
revision = "0030_add_syncshell_rate_limit_and_expiry"
down_revision = "0029_add_syncshell_tables"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column("syncshell_pairings", sa.Column("expires_at", sa.DateTime(), nullable=False))
    op.create_index(
        "ix_syncshell_pairings_expires_at",
        "syncshell_pairings",
        ["expires_at"],
        unique=False,
    )
    op.create_table(
        "syncshell_rate_limits",
        sa.Column("user_id", BIGINT(unsigned=True), sa.ForeignKey("users.id"), primary_key=True),
        sa.Column("requests", sa.Integer(), nullable=False, server_default="0"),
        sa.Column("window_start", sa.DateTime(), nullable=False),
    )


def downgrade() -> None:
    op.drop_table("syncshell_rate_limits")
    op.drop_index("ix_syncshell_pairings_expires_at", table_name="syncshell_pairings")
    op.drop_column("syncshell_pairings", "expires_at")
