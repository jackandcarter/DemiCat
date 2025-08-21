import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision = "0008_add_signup_presets"
down_revision = "0007_add_presence_table"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "signup_presets",
        sa.Column("id", sa.Integer(), nullable=False),
        sa.Column("guild_id", sa.Integer(), nullable=False),
        sa.Column("name", sa.String(length=255), nullable=False),
        sa.Column("buttons_json", sa.Text(), nullable=False),
        sa.ForeignKeyConstraint(["guild_id"], ["guilds.id"]),
        sa.PrimaryKeyConstraint("id"),
    )
    op.create_index(
        "ix_signup_presets_guild_id",
        "signup_presets",
        ["guild_id"],
    )


def downgrade() -> None:
    op.drop_index("ix_signup_presets_guild_id", table_name="signup_presets")
    op.drop_table("signup_presets")
