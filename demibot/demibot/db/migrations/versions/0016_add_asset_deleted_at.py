"""add asset deleted_at

Revision ID: 0016_add_asset_deleted_at
Revises: 0015_add_fc_user_last_pull_at
Create Date: 2025-09-21
"""

from alembic import op
import sqlalchemy as sa

revision = "0016_add_asset_deleted_at"
down_revision = "0015_add_fc_user_last_pull_at"
branch_labels = None
depends_on = None

def upgrade() -> None:
    op.add_column("asset", sa.Column("deleted_at", sa.DateTime(), nullable=True))


def downgrade() -> None:
    op.drop_column("asset", "deleted_at")
