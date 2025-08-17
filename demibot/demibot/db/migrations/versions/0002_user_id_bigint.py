from __future__ import annotations

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision = "0002_user_id_bigint"
down_revision = "0001_initial"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.alter_column("users", "id", existing_type=sa.Integer(), type_=sa.BigInteger())
    op.alter_column(
        "user_keys",
        "user_id",
        existing_type=sa.Integer(),
        type_=sa.BigInteger(),
    )
    op.alter_column(
        "memberships",
        "user_id",
        existing_type=sa.Integer(),
        type_=sa.BigInteger(),
    )
    op.alter_column(
        "messages",
        "author_id",
        existing_type=sa.Integer(),
        type_=sa.BigInteger(),
    )
    op.alter_column(
        "attendance",
        "user_id",
        existing_type=sa.Integer(),
        type_=sa.BigInteger(),
    )


def downgrade() -> None:
    op.alter_column("users", "id", existing_type=sa.BigInteger(), type_=sa.Integer())
    op.alter_column(
        "user_keys",
        "user_id",
        existing_type=sa.BigInteger(),
        type_=sa.Integer(),
    )
    op.alter_column(
        "memberships",
        "user_id",
        existing_type=sa.BigInteger(),
        type_=sa.Integer(),
    )
    op.alter_column(
        "messages",
        "author_id",
        existing_type=sa.BigInteger(),
        type_=sa.Integer(),
    )
    op.alter_column(
        "attendance",
        "user_id",
        existing_type=sa.BigInteger(),
        type_=sa.Integer(),
    )

