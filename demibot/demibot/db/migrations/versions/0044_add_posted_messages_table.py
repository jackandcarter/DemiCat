"""add posted messages table

Revision ID: 0044_add_posted_messages_table
Revises: 0043_add_events_table
Create Date: 2025-01-01 00:00:00.000000
"""

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import mysql

# revision identifiers, used by Alembic.
revision: str = "0044_add_posted_messages_table"
down_revision: str = "0043_add_events_table"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "posted_messages",
        sa.Column("id", sa.Integer(), primary_key=True, autoincrement=True),
        sa.Column("guild_id", sa.Integer(), sa.ForeignKey("guilds.id"), nullable=False),
        sa.Column("channel_id", mysql.BIGINT(unsigned=True), nullable=False),
        sa.Column("local_message_id", mysql.BIGINT(unsigned=True), nullable=False),
        sa.Column("discord_message_id", mysql.BIGINT(unsigned=True), nullable=False),
        sa.Column("webhook_url", sa.String(length=255), nullable=True),
    )
    op.create_index(
        "ix_posted_messages_guild_channel",
        "posted_messages",
        ["guild_id", "channel_id"],
    )
    op.create_unique_constraint(
        "uq_posted_messages_guild_local",
        "posted_messages",
        ["guild_id", "local_message_id"],
    )
    op.create_unique_constraint(
        "uq_posted_messages_discord",
        "posted_messages",
        ["discord_message_id"],
    )


def downgrade() -> None:
    op.drop_constraint("uq_posted_messages_discord", "posted_messages", type_="unique")
    op.drop_constraint("uq_posted_messages_guild_local", "posted_messages", type_="unique")
    op.drop_index("ix_posted_messages_guild_channel", table_name="posted_messages")
    op.drop_table("posted_messages")
