"""add status text to presences

Revision ID: 0045_add_presence_status_text
Revises: 0044_add_posted_messages_table
Create Date: 2025-02-14 00:00:00.000000
"""

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision: str = "0045_add_presence_status_text"
down_revision: str = "0044_add_posted_messages_table"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column(
        "presences",
        sa.Column("status_text", sa.String(length=128), nullable=True),
    )


def downgrade() -> None:
    op.drop_column("presences", "status_text")
