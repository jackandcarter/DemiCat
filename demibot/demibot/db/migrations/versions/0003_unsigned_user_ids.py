from __future__ import annotations

import sqlalchemy as sa
from sqlalchemy.dialects import mysql
from alembic import op

# revision identifiers, used by Alembic.
revision = "0003_unsigned_user_ids"
down_revision = "0002_user_id_bigint"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.drop_constraint("user_keys_ibfk_1", "user_keys", type_="foreignkey")
    op.drop_constraint("memberships_ibfk_2", "memberships", type_="foreignkey")
    op.drop_constraint("messages_ibfk_2", "messages", type_="foreignkey")
    op.drop_constraint("attendance_ibfk_1", "attendance", type_="foreignkey")

    op.alter_column(
        "users",
        "id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
        nullable=False,
    )
    op.alter_column(
        "user_keys",
        "user_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
        nullable=False,
    )
    op.alter_column(
        "memberships",
        "user_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
        nullable=False,
    )
    op.alter_column(
        "messages",
        "author_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
        nullable=False,
    )
    op.alter_column(
        "attendance",
        "user_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
        nullable=False,
    )

    op.create_foreign_key(
        "user_keys_ibfk_1", "user_keys", "users", ["user_id"], ["id"]
    )
    op.create_foreign_key(
        "memberships_ibfk_2", "memberships", "users", ["user_id"], ["id"]
    )
    op.create_foreign_key(
        "messages_ibfk_2", "messages", "users", ["author_id"], ["id"]
    )
    op.create_foreign_key(
        "attendance_ibfk_1", "attendance", "users", ["user_id"], ["id"]
    )


def downgrade() -> None:
    op.drop_constraint("user_keys_ibfk_1", "user_keys", type_="foreignkey")
    op.drop_constraint("memberships_ibfk_2", "memberships", type_="foreignkey")
    op.drop_constraint("messages_ibfk_2", "messages", type_="foreignkey")
    op.drop_constraint("attendance_ibfk_1", "attendance", type_="foreignkey")

    op.alter_column(
        "users",
        "id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
        nullable=False,
    )
    op.alter_column(
        "user_keys",
        "user_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
        nullable=False,
    )
    op.alter_column(
        "memberships",
        "user_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
        nullable=False,
    )
    op.alter_column(
        "messages",
        "author_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
        nullable=False,
    )
    op.alter_column(
        "attendance",
        "user_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
        nullable=False,
    )

    op.create_foreign_key(
        "user_keys_ibfk_1", "user_keys", "users", ["user_id"], ["id"]
    )
    op.create_foreign_key(
        "memberships_ibfk_2", "memberships", "users", ["user_id"], ["id"]
    )
    op.create_foreign_key(
        "messages_ibfk_2", "messages", "users", ["author_id"], ["id"]
    )
    op.create_foreign_key(
        "attendance_ibfk_1", "attendance", "users", ["user_id"], ["id"]
    )
