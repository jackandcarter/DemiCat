"""normalize awaiting_confirm status spelling

Revision ID: 0060_fix_request_status_spelling
Revises: 0059_remove_syncshell_tables
Create Date: 2025-01-05
"""

from __future__ import annotations

import sqlalchemy as sa
from alembic import op


revision = "0060_fix_request_status_spelling"
down_revision = "0059_remove_syncshell_tables"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.execute(
        sa.text(
            """
            UPDATE requests
            SET status = 'awaiting_confirm'
            WHERE status = 'awaiting_confirmation'
            """
        )
    )


def downgrade() -> None:
    op.execute(
        sa.text(
            """
            UPDATE requests
            SET status = 'awaiting_confirmation'
            WHERE status = 'awaiting_confirm'
            """
        )
    )
