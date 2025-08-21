import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision = "0005_add_role_discord_role_id"
down_revision = "0004_drop_single_channel_fields"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column(
        "roles", sa.Column("discord_role_id", sa.BigInteger(), nullable=False)
    )
    op.create_index(
        op.f("ix_roles_discord_role_id"), "roles", ["discord_role_id"], unique=True
    )


def downgrade() -> None:
    op.drop_index(op.f("ix_roles_discord_role_id"), table_name="roles")
    op.drop_column("roles", "discord_role_id")
