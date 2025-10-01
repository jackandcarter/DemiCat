"""0055_add_syncshell_transfer_budgets

Revision ID: 0055_add_syncshell_transfer_budgets
Revises: 0054_add_syncshell_member_scope
Create Date: 2024-07-02 00:00:00.000000
"""

from __future__ import annotations

from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import mysql


# revision identifiers, used by Alembic.
revision: str = "0055_add_syncshell_transfer_budgets"
down_revision: Union[str, None] = "0054_add_syncshell_member_scope"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        "syncshell_transfer_budgets",
        sa.Column("user_id", mysql.BIGINT(unsigned=True), primary_key=True),
        sa.Column("window_start", sa.DateTime(), nullable=False),
        sa.Column(
            "used_bytes",
            sa.Integer(),
            nullable=False,
            server_default=sa.text("0"),
        ),
        sa.ForeignKeyConstraint(["user_id"], ["users.id"]),
    )


def downgrade() -> None:
    op.drop_table("syncshell_transfer_budgets")
