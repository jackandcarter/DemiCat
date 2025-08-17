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
    op.alter_column("users", "id", existing_type=sa.BigInteger(), type_=mysql.BIGINT(unsigned=True))
    op.alter_column("user_keys", "user_id", existing_type=sa.BigInteger(), type_=mysql.BIGINT(unsigned=True))
    op.alter_column("memberships", "user_id", existing_type=sa.BigInteger(), type_=mysql.BIGINT(unsigned=True))
    op.alter_column("messages", "author_id", existing_type=sa.BigInteger(), type_=mysql.BIGINT(unsigned=True))
    op.alter_column("attendance", "user_id", existing_type=sa.BigInteger(), type_=mysql.BIGINT(unsigned=True))

def downgrade() -> None:
    op.alter_column("users", "id", existing_type=mysql.BIGINT(unsigned=True), type_=sa.BigInteger())
    op.alter_column("user_keys", "user_id", existing_type=mysql.BIGINT(unsigned=True), type_=sa.BigInteger())
    op.alter_column("memberships", "user_id", existing_type=mysql.BIGINT(unsigned=True), type_=sa.BigInteger())
    op.alter_column("messages", "author_id", existing_type=mysql.BIGINT(unsigned=True), type_=sa.BigInteger())
    op.alter_column("attendance", "user_id", existing_type=mysql.BIGINT(unsigned=True), type_=sa.BigInteger())
