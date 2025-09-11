from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects.mysql import BIGINT


revision = "0039_add_officer_role_ids"
down_revision = "0038_add_request_tombstones_table"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column("guild_config", sa.Column("officer_role_ids", sa.Text(), nullable=True))
    bind = op.get_bind()
    bind.execute(
        sa.text(
            "UPDATE guild_config SET officer_role_ids = CAST(officer_role_id AS CHAR) "
            "WHERE officer_role_id IS NOT NULL"
        )
    )
    op.drop_column("guild_config", "officer_role_id")


def downgrade() -> None:
    op.add_column(
        "guild_config",
        sa.Column("officer_role_id", BIGINT(unsigned=True), nullable=True),
    )
    bind = op.get_bind()
    bind.execute(
        sa.text(
            "UPDATE guild_config SET officer_role_id = CAST(SUBSTRING_INDEX(officer_role_ids, ',', 1) AS UNSIGNED) "
            "WHERE officer_role_ids IS NOT NULL AND officer_role_ids != ''"
        )
    )
    op.drop_column("guild_config", "officer_role_ids")

