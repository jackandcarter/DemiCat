from alembic import op
import sqlalchemy as sa

revision = '0031_add_membership_nick_avatar'
down_revision = '0030_add_syncshell_rate_limit_and_expiry'
branch_labels = None
depends_on = None

def upgrade() -> None:
    op.add_column('memberships', sa.Column('nickname', sa.String(length=255), nullable=True))
    op.add_column('memberships', sa.Column('avatar_url', sa.String(length=255), nullable=True))

def downgrade() -> None:
    op.drop_column('memberships', 'avatar_url')
    op.drop_column('memberships', 'nickname')
