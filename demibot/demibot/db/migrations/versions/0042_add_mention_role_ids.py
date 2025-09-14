import sqlalchemy as sa
from alembic import op

revision = "0042_add_mention_role_ids"
down_revision = "0041_add_unicode_emojis_table"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column("guild_config", sa.Column("mention_role_ids", sa.Text(), nullable=True))


def downgrade() -> None:
    op.drop_column("guild_config", "mention_role_ids")

