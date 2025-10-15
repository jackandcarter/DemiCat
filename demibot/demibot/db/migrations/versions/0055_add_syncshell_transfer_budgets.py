"""0055_add_syncshell_transfer_budgets

Revision ID: 0055_add_syncshell_transfer_budgets
Revises: 0054_expand_channelkind_and_status_text
Create Date: 2025-10-14 23:50:00.000000
"""

from __future__ import annotations

from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects.mysql import BIGINT


# revision identifiers, used by Alembic.
revision: str = "0055_add_syncshell_transfer_budgets"
down_revision: Union[str, None] = "0054_expand_channelkind_and_status_text"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        "syncshell_transfer_budgets",
        sa.Column(
            "user_id",
            BIGINT(unsigned=True),
            sa.ForeignKey("users.id"),
            primary_key=True,
        ),
        sa.Column("window_start", sa.DateTime(), nullable=False),
        sa.Column(
            "used_bytes",
            BIGINT(unsigned=True),
            nullable=False,
            server_default="0",
        ),
    )


def downgrade() -> None:
    op.drop_table("syncshell_transfer_budgets")

