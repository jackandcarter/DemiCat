"""expand request status to include full workflow

Revision ID: 0019_expand_request_status
Revises: 0018_expand_version_num_to_128_chars
Create Date: 2025-10-29
"""

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import mysql

revision = "0019_expand_request_status"
down_revision = "0018_expand_version_num_to_128_chars"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.alter_column(
        "requests",
        "status",
        existing_type=mysql.ENUM("open", "approved", "denied", name="request_status"),
        type_=mysql.ENUM(
            "open",
            "claimed",
            "in_progress",
            "awaiting_confirm",
            "completed",
            "cancelled",
            "approved",
            "denied",
            name="request_status",
        ),
        nullable=False,
    )


def downgrade() -> None:
    op.alter_column(
        "requests",
        "status",
        existing_type=mysql.ENUM(
            "open",
            "claimed",
            "in_progress",
            "awaiting_confirm",
            "completed",
            "cancelled",
            "approved",
            "denied",
            name="request_status",
        ),
        type_=mysql.ENUM("open", "approved", "denied", name="request_status"),
        nullable=False,
    )
