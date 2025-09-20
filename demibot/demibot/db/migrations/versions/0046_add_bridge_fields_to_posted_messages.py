"""add nonce and embed json to posted messages

Revision ID: 0046_add_bridge_fields_to_posted_messages
Revises: 0045_add_presence_status_text
Create Date: 2025-03-01 00:00:00.000000
"""

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision: str = "0046_add_bridge_fields_to_posted_messages"
down_revision: str = "0045_add_presence_status_text"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column(
        "posted_messages",
        sa.Column("embed_json", sa.Text(), nullable=True),
    )
    op.add_column(
        "posted_messages",
        sa.Column("nonce", sa.String(length=64), nullable=True),
    )
    op.create_unique_constraint(
        "uq_posted_messages_guild_channel_nonce",
        "posted_messages",
        ["guild_id", "channel_id", "nonce"],
    )


def downgrade() -> None:
    op.drop_constraint(
        "uq_posted_messages_guild_channel_nonce",
        "posted_messages",
        type_="unique",
    )
    op.drop_column("posted_messages", "nonce")
    op.drop_column("posted_messages", "embed_json")
