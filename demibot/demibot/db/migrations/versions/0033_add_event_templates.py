"""add event templates table

Revision ID: 0033_add_event_templates
Revises: 0032_add_guild_channel_webhook_url
Create Date: 2025-01-01 00:00:00.000000
"""

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision = '0033_add_event_templates'
down_revision = '0032_add_guild_channel_webhook_url'
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        'event_templates',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('guild_id', sa.Integer(), nullable=False),
        sa.Column('name', sa.String(length=255), nullable=False),
        sa.Column('description', sa.Text(), nullable=True),
        sa.Column('payload_json', sa.Text(), nullable=False),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.Column('updated_at', sa.DateTime(), nullable=False),
        sa.ForeignKeyConstraint(['guild_id'], ['guilds.id']),
        sa.PrimaryKeyConstraint('id'),
        sa.UniqueConstraint('guild_id', 'name', name='uq_event_templates_guild_name'),
    )
    op.create_index(
        'ix_event_templates_guild_id',
        'event_templates',
        ['guild_id'],
    )


def downgrade() -> None:
    op.drop_index('ix_event_templates_guild_id', table_name='event_templates')
    op.drop_table('event_templates')
