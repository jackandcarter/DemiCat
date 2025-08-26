"""add fc_user last_pull_at

Revision ID: 0015_add_fc_user_last_pull_at
Revises: 0014_add_fc_user_settings
Create Date: 2025-09-21
"""
from __future__ import annotations

from alembic import op
import sqlalchemy as sa

revision = "0015_add_fc_user_last_pull_at"
down_revision = "0014_add_fc_user_settings"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column("fc_user", sa.Column("last_pull_at", sa.DateTime(), nullable=True))


def downgrade() -> None:
    op.drop_column("fc_user", "last_pull_at")
