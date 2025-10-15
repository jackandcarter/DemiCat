"""0054_expand_channelkind_and_status_text

Revision ID: 0054_expand_channelkind_and_status_text
Revises: 0053_add_notepad_tables
Create Date: 2024-10-15 00:00:01.000000
"""

from __future__ import annotations

from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


# revision identifiers, used by Alembic.
revision = "0054_expand_channelkind_and_status_text"
down_revision = "0053_add_notepad_tables"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.execute(
        "ALTER TABLE guild_channels MODIFY kind "
        "ENUM('chat','event','fc_chat','officer_chat','officer_visible','requests') NOT NULL"
    )

    op.alter_column(
        "presences",
        "status_text",
        existing_type=sa.String(length=128),
        type_=sa.String(length=255),
        existing_nullable=True,
    )


def downgrade() -> None:
    op.execute(
        "ALTER TABLE guild_channels MODIFY kind "
        "ENUM('chat','event','fc_chat','officer_chat','officer_visible') NOT NULL"
    )

    op.alter_column(
        "presences",
        "status_text",
        existing_type=sa.String(length=255),
        type_=sa.String(length=128),
        existing_nullable=True,
    )
