import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision = "0004_drop_single_channel_fields"
down_revision = "0003_unsigned_user_ids"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.drop_column("guild_config", "event_channel_id")
    op.drop_column("guild_config", "fc_chat_channel_id")
    op.drop_column("guild_config", "officer_chat_channel_id")
    op.drop_column("guild_config", "chat_role_id")


def downgrade() -> None:
    op.add_column("guild_config", sa.Column("event_channel_id", sa.BigInteger(), nullable=True))
    op.add_column("guild_config", sa.Column("fc_chat_channel_id", sa.BigInteger(), nullable=True))
    op.add_column("guild_config", sa.Column("officer_chat_channel_id", sa.BigInteger(), nullable=True))
    op.add_column("guild_config", sa.Column("chat_role_id", sa.BigInteger(), nullable=True))

