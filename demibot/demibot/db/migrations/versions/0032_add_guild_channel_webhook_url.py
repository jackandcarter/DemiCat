"""add webhook_url column to guild channels

Revision ID: 0032_add_guild_channel_webhook_url
Revises: 0031_add_membership_nick_avatar
Create Date: 2025-01-01 00:00:00.000000
"""

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision = '0032_add_guild_channel_webhook_url'
down_revision = '0031_add_membership_nick_avatar'
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column('guild_channels', sa.Column('webhook_url', sa.String(length=255), nullable=True))


def downgrade() -> None:
    op.drop_column('guild_channels', 'webhook_url')
