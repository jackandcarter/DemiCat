"""0058_drop_syncshell_tables

Revision ID: 0058_drop_syncshell_tables
Revises: 0057_expand_presence_status_text_to_text
Create Date: 2025-10-20 00:00:00.000000
"""

from __future__ import annotations

from typing import Sequence, Union

# revision identifiers, used by Alembic.
revision: str = "0058_drop_syncshell_tables"
down_revision: Union[str, None] = "0057_expand_presence_status_text_to_text"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    """Intentionally left blank.

    SyncShell tables are ignored going forward, so this migration performs no
    schema changes to keep the history linear without touching existing data.
    """

    return None


def downgrade() -> None:
    """Downgrade is also a no-op."""

    return None

