"""0056_add_requests_channel_kind

Revision ID: 0056_add_requests_channel_kind
Revises: 0054_expand_channelkind_and_status_text
Create Date: 2024-10-15 00:05:01.000000
"""

from __future__ import annotations

from typing import Sequence, Union

from alembic import op


# revision identifiers, used by Alembic.
revision: str = "0056_add_requests_channel_kind"
down_revision: Union[str, None] = "0054_expand_channelkind_and_status_text"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.execute(
        "ALTER TABLE guild_channels MODIFY kind "
        "ENUM('chat','event','fc_chat','officer_chat','officer_visible','requests') NOT NULL"
    )


def downgrade() -> None:
    op.execute(
        "ALTER TABLE guild_channels MODIFY kind "
        "ENUM('chat','event','fc_chat','officer_chat','officer_visible') NOT NULL"
    )
