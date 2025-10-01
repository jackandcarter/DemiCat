"""0054_add_syncshell_member_scope

Revision ID: 0054_add_syncshell_member_scope
Revises: 0053_add_notepad_tables
Create Date: 2024-07-02 00:00:00.000000
"""

from __future__ import annotations

from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


# revision identifiers, used by Alembic.
revision: str = "0054_add_syncshell_member_scope"
down_revision: Union[str, None] = "0053_add_notepad_tables"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column(
        "syncshell_members",
        sa.Column("scope", sa.Integer(), nullable=False, server_default=sa.text("1")),
    )
    op.execute("UPDATE syncshell_members SET scope = 1 WHERE scope IS NULL")
    op.alter_column("syncshell_members", "scope", server_default=None)


def downgrade() -> None:
    op.drop_column("syncshell_members", "scope")
