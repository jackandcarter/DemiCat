"""add role metadata fields

Revision ID: 0047_add_role_metadata_fields
Revises: 0046_add_bridge_fields_to_posted_messages
Create Date: 2025-03-15 00:00:00.000000
"""

from __future__ import annotations

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision: str = "0047_add_role_metadata_fields"
down_revision: str = "0046_add_bridge_fields_to_posted_messages"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column(
        "roles",
        sa.Column("position", sa.Integer(), nullable=False, server_default="0"),
    )
    op.add_column(
        "roles",
        sa.Column(
            "hoist",
            sa.Boolean(),
            nullable=False,
            server_default=sa.false(),
        ),
    )
    op.add_column(
        "roles",
        sa.Column(
            "premium_subscriber",
            sa.Boolean(),
            nullable=False,
            server_default=sa.false(),
        ),
    )


def downgrade() -> None:
    op.drop_column("roles", "premium_subscriber")
    op.drop_column("roles", "hoist")
    op.drop_column("roles", "position")
