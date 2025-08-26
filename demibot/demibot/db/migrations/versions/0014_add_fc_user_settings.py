"""add fc_user settings and consent flag

Revision ID: 0014_add_fc_user_settings
Revises: 0013_add_fc_and_asset_tables
Create Date: 2025-09-01
"""
from __future__ import annotations

from alembic import op
import sqlalchemy as sa

revision = "0014_add_fc_user_settings"
down_revision = "0013_add_fc_and_asset_tables"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column("fc_user", sa.Column("settings", sa.Text(), nullable=True))
    op.add_column(
        "fc_user",
        sa.Column(
            "consent_sync",
            sa.Boolean(),
            nullable=False,
            server_default=sa.false(),
        ),
    )


def downgrade() -> None:
    op.drop_column("fc_user", "consent_sync")
    op.drop_column("fc_user", "settings")
