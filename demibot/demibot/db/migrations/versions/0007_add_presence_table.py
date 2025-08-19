import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision = "0007_add_presence_table"
down_revision = "0006_add_guild_channel_name"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "presences",
        sa.Column("guild_id", sa.BigInteger(), nullable=False),
        sa.Column("user_id", sa.BigInteger(), nullable=False),
        sa.Column("status", sa.String(length=16), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.PrimaryKeyConstraint("guild_id", "user_id"),
    )
    op.create_index(
        "ix_presences_guild_id_status",
        "presences",
        ["guild_id", "status"],
    )


def downgrade() -> None:
    op.drop_index("ix_presences_guild_id_status", table_name="presences")
    op.drop_table("presences")
