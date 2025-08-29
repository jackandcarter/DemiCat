from __future__ import annotations

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision = "0025_add_author_avatar_url"
down_revision = "0024_autoincrement_user_id"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column(
        "messages",
        sa.Column("author_avatar_url", sa.String(255), nullable=True),
    )


def downgrade() -> None:
    op.drop_column("messages", "author_avatar_url")
