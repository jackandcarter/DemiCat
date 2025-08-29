from __future__ import annotations

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects.mysql import BIGINT

# revision identifiers, used by Alembic.
revision = "0023_add_recurring_events"
down_revision = "0022_add_asset_uploader"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "recurring_events",
        sa.Column("id", BIGINT(unsigned=True), primary_key=True),
        sa.Column("guild_id", sa.Integer(), sa.ForeignKey("guilds.id"), nullable=False),
        sa.Column("channel_id", BIGINT(unsigned=True), nullable=False),
        sa.Column("repeat", sa.String(length=16), nullable=False),
        sa.Column("next_post_at", sa.DateTime(), nullable=False),
        sa.Column("payload_json", sa.Text(), nullable=False),
    )


def downgrade() -> None:
    op.drop_table("recurring_events")
