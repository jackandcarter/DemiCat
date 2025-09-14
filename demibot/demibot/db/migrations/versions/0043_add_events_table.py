"""add events table

Revision ID: 0043_add_events_table
Revises: 0042_add_mention_role_ids
Create Date: 2025-01-01 00:00:00.000000
"""

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import mysql

# revision identifiers, used by Alembic.
revision: str = "0043_add_events_table"
down_revision: str = "0042_add_mention_role_ids"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "events",
        sa.Column(
            "discord_message_id",
            mysql.BIGINT(unsigned=True),
            primary_key=True,
        ),
        sa.Column("channel_id", mysql.BIGINT(unsigned=True), nullable=False),
        sa.Column("guild_id", sa.Integer(), sa.ForeignKey("guilds.id"), nullable=False),
        sa.Column("embeds", sa.JSON(), nullable=True),
        sa.Column("attachments", sa.JSON(), nullable=True),
        sa.Column("created_at", sa.DateTime(), nullable=False),
    )
    op.create_index("ix_events_channel_id", "events", ["channel_id"])


def downgrade() -> None:
    op.drop_index("ix_events_channel_id", table_name="events")
    op.drop_table("events")
