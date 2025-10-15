"""0054_add_syncshell_member_scope (legacy placeholder)

Revision ID: 0054_add_syncshell_member_scope
Revises: 0053_add_notepad_tables
Create Date: 2024-10-15 00:00:00.000000
"""

from __future__ import annotations
from typing import Sequence, Union

# Alembic imports
from alembic import op  # noqa: F401 (kept to look like a normal migration)

# revision identifiers, used by Alembic.
revision: str = "0054_add_syncshell_member_scope"
down_revision: Union[str, None] = "0053_add_notepad_tables"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    # Intentionally no-op. This placeholder exists to reconcile history
    # when previous deployments used a different 0054 id.
    pass


def downgrade() -> None:
    # No-op; reversing a placeholder would do nothing as well.
    pass
