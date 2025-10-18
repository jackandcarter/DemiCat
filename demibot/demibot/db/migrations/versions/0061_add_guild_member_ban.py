"""0061_add_guild_member_ban

Revision ID: 0061_add_guild_member_ban
Revises: 0060_fix_request_status_spelling
Create Date: 2024-07-20 00:00:00.000000
"""

from __future__ import annotations

from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects.mysql import BIGINT


# revision identifiers, used by Alembic.
revision: str = "0061_add_guild_member_ban"
down_revision: Union[str, None] = "0060_fix_request_status_spelling"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        "guild_member_ban",
        sa.Column("id", sa.Integer(), nullable=False),
        sa.Column("guild_id", sa.Integer(), nullable=False),
        sa.Column("discord_user_id", BIGINT(unsigned=True), nullable=False),
        sa.Column("created_at", sa.DateTime(), server_default=sa.func.now(), nullable=False),
        sa.ForeignKeyConstraint(["guild_id"], ["guilds.id"]),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("guild_id", "discord_user_id", name="uq_guild_member_ban_guild_user"),
    )


def downgrade() -> None:
    op.drop_table("guild_member_ban")
