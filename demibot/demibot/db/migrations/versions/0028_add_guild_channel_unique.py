"""add unique constraint to guild channels

Revision ID: 0028_add_guild_channel_unique
Revises: 0027_add_message_metadata_fields
Create Date: 2025-01-01 00:00:00.000000
"""

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision = '0028_add_guild_channel_unique'
down_revision = '0027_add_message_metadata_fields'
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_unique_constraint(
        'uq_guild_channels_guild_channel',
        'guild_channels',
        ['guild_id', 'channel_id'],
    )


def downgrade() -> None:
    op.drop_constraint(
        'uq_guild_channels_guild_channel',
        'guild_channels',
        type_='unique',
    )
