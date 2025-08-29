"""expand version_num to 128 chars

Revision ID: 0018_expand_version_num_to_128_chars
Revises: 0017_user_character_world
Create Date: 2025-10-28
"""

from alembic import op
import sqlalchemy as sa

revision = "0018_expand_version_num_to_128_chars"
down_revision = "0017_user_character_world"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.alter_column(
        "alembic_version",
        "version_num",
        existing_type=sa.String(length=64),
        type_=sa.String(length=128),
    )


def downgrade() -> None:
    op.alter_column(
        "alembic_version",
        "version_num",
        existing_type=sa.String(length=128),
        type_=sa.String(length=64),
    )
