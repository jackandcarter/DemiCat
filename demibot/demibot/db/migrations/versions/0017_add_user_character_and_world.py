"""add user character_name and world

Revision ID: 0017_add_user_character_and_world
Revises: 0016_add_asset_deleted_at
Create Date: 2025-09-21
"""

from alembic import op
import sqlalchemy as sa

revision = "0017_add_user_character_and_world"
down_revision = "0016_add_asset_deleted_at"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column("users", sa.Column("character_name", sa.String(length=255), nullable=True))
    op.add_column("users", sa.Column("world", sa.String(length=32), nullable=True))


def downgrade() -> None:
    op.drop_column("users", "character_name")
    op.drop_column("users", "world")
