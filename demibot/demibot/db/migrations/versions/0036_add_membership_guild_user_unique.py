from alembic import op
import sqlalchemy as sa

revision = '0036_add_membership_guild_user_unique'
down_revision = '0035_rename_attendance_event_signups'
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_index(
        'uq_memberships_guild_user',
        'memberships',
        ['guild_id', 'user_id'],
        unique=True,
    )


def downgrade() -> None:
    op.drop_index('uq_memberships_guild_user', table_name='memberships')
