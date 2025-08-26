"""add assignee and hq fields to requests tables

Revision ID: 0012_add_request_assignee_and_hq
Revises: 0011_add_requests_tables
Create Date: 2024-06-01
"""

from __future__ import annotations

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects.mysql import BIGINT

# revision identifiers, used by Alembic.
revision = "0012_add_request_assignee_and_hq"
down_revision = "0011_add_requests_tables"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column(
        "requests",
        sa.Column("assignee_id", BIGINT(unsigned=True), sa.ForeignKey("users.id"), nullable=True),
    )
    op.create_index("ix_requests_assignee_id", "requests", ["assignee_id"])
    op.add_column(
        "request_items",
        sa.Column("hq", sa.Boolean(), nullable=False, server_default=sa.text("0")),
    )


def downgrade() -> None:
    op.drop_column("request_items", "hq")
    op.drop_index("ix_requests_assignee_id", table_name="requests")
    op.drop_column("requests", "assignee_id")
