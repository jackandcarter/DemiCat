from alembic import op
import sqlalchemy as sa

revision = '0037_add_reactions_json_to_messages'
down_revision = '0036_add_membership_guild_user_unique'
branch_labels = None
depends_on = None

def upgrade() -> None:
    op.add_column('messages', sa.Column('reactions_json', sa.Text(), nullable=True))


def downgrade() -> None:
    op.drop_column('messages', 'reactions_json')
