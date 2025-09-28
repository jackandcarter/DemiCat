"""0049_add_installation_asset_hash

Revision ID: 0049_add_installation_asset_hash
Revises: 0048_add_membership_banner_and_accent
Create Date: 2024-05-14 00:00:00.000000
"""

from __future__ import annotations

from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


# revision identifiers, used by Alembic.
revision: str = "0049_add_installation_asset_hash"
down_revision: Union[str, None] = "0048_add_membership_banner_and_accent"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column(
        "user_installation",
        sa.Column("asset_hash", sa.String(length=64), nullable=True),
    )

    user_installation = sa.table(
        "user_installation",
        sa.column("id", sa.Integer),
        sa.column("asset_id", sa.Integer),
        sa.column("asset_hash", sa.String(length=64)),
    )
    asset = sa.table(
        "asset",
        sa.column("id", sa.Integer),
        sa.column("hash", sa.String(length=64)),
    )

    conn = op.get_bind()
    rows = conn.execute(
        sa.select(user_installation.c.id, asset.c.hash)
        .select_from(user_installation.join(asset, user_installation.c.asset_id == asset.c.id))
    ).all()

    for row_id, asset_hash in rows:
        if asset_hash:
            conn.execute(
                sa.update(user_installation)
                .where(user_installation.c.id == row_id)
                .values(asset_hash=asset_hash)
            )


def downgrade() -> None:
    op.drop_column("user_installation", "asset_hash")
