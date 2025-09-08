from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects.mysql import BIGINT

revision = '0038_add_request_tombstones_table'
down_revision = '0037_add_reactions_json_to_messages'
branch_labels = None
depends_on = None

def upgrade() -> None:
    op.create_table(
        'request_tombstones',
        sa.Column('request_id', BIGINT(unsigned=True), primary_key=True),
        sa.Column('guild_id', BIGINT(unsigned=True), sa.ForeignKey('guilds.id'), nullable=False),
        sa.Column('version', sa.Integer(), nullable=False),
        sa.Column('deleted_at', sa.DateTime(), nullable=True),
    )
    op.create_index('ix_request_tombstones_deleted_at', 'request_tombstones', ['deleted_at'])


def downgrade() -> None:
    op.drop_index('ix_request_tombstones_deleted_at', table_name='request_tombstones')
    op.drop_table('request_tombstones')
