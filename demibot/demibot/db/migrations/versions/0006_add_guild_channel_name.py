import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision = "0006_add_guild_channel_name"
down_revision = "0005_add_role_discord_role_id"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column(
        "guild_channels", sa.Column("name", sa.String(length=255), nullable=True)
    )


def downgrade() -> None:
    op.drop_column("guild_channels", "name")

