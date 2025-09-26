"""add membership banner and accent fields

Revision ID: 0048_add_membership_banner_and_accent
Revises: 0047_add_role_metadata_fields
Create Date: 2025-03-15 00:00:00.000000
"""

from __future__ import annotations

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision: str = "0048_add_membership_banner_and_accent"
down_revision: str = "0047_add_role_metadata_fields"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column(
        "memberships",
        sa.Column("banner_url", sa.String(length=255), nullable=True),
    )
    op.add_column(
        "memberships",
        sa.Column("accent_color", sa.Integer(), nullable=True),
    )


def downgrade() -> None:
    op.drop_column("memberships", "accent_color")
    op.drop_column("memberships", "banner_url")
