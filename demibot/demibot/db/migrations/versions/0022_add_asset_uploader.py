from __future__ import annotations

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects.mysql import BIGINT

# revision identifiers, used by Alembic.
revision = "0022_add_asset_uploader"
down_revision = "0021_unsigned_discord_ids"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column(
        "asset",
        sa.Column(
            "uploader_id",
            BIGINT(unsigned=True),
            sa.ForeignKey("users.id", ondelete="SET NULL"),
            nullable=True,
        ),
    )
    op.create_index("ix_asset_uploader_id", "asset", ["uploader_id"])


def downgrade() -> None:
    op.drop_index("ix_asset_uploader_id", table_name="asset")
    op.drop_column("asset", "uploader_id")
