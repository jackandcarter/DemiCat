from __future__ import annotations

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision = "0026_add_attachments_json_to_messages"
down_revision = "0025_add_author_avatar_url"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column("messages", sa.Column("attachments_json", sa.Text(), nullable=True))


def downgrade() -> None:
    op.drop_column("messages", "attachments_json")
