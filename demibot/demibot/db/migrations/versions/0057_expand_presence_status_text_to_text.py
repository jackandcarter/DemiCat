"""0057_expand_presence_status_text_to_text

Revision ID: 0057_expand_presence_status_text_to_text
Revises: 0056_add_requests_channel_kind
Create Date: 2025-10-15 00:10:01.000000
"""

from __future__ import annotations

from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


# revision identifiers, used by Alembic.
revision: str = "0057_expand_presence_status_text_to_text"
down_revision: Union[str, None] = "0056_add_requests_channel_kind"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.alter_column(
        "presences",
        "status_text",
        existing_type=sa.String(length=255),
        type_=sa.Text(),
        existing_nullable=True,
    )


def downgrade() -> None:
    op.alter_column(
        "presences",
        "status_text",
        existing_type=sa.Text(),
        type_=sa.String(length=255),
        existing_nullable=True,
    )
