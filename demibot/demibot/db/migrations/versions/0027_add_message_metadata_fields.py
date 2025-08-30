"""add message metadata fields

Revision ID: 0027_add_message_metadata_fields
Revises: 0026_add_attachments_json_to_messages
Create Date: 2024-09-20 00:00:00.000000"""

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision = '0027_add_message_metadata_fields'
down_revision = '0026_add_attachments_json_to_messages'
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column('messages', sa.Column('content', sa.Text(), nullable=True))
    op.add_column('messages', sa.Column('author_json', sa.Text(), nullable=True))
    op.add_column('messages', sa.Column('embeds_json', sa.Text(), nullable=True))
    op.add_column('messages', sa.Column('mentions_json', sa.Text(), nullable=True))
    op.add_column('messages', sa.Column('reference_json', sa.Text(), nullable=True))
    op.add_column('messages', sa.Column('components_json', sa.Text(), nullable=True))
    op.add_column('messages', sa.Column('edited_timestamp', sa.DateTime(), nullable=True))


def downgrade() -> None:
    op.drop_column('messages', 'edited_timestamp')
    op.drop_column('messages', 'components_json')
    op.drop_column('messages', 'reference_json')
    op.drop_column('messages', 'mentions_json')
    op.drop_column('messages', 'embeds_json')
    op.drop_column('messages', 'author_json')
    op.drop_column('messages', 'content')
