from __future__ import annotations

from alembic import op

# revision identifiers, used by Alembic.
revision = "0024_autoincrement_user_id"
down_revision = "0023_add_recurring_events"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.execute("ALTER TABLE users MODIFY id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT")


def downgrade() -> None:
    op.execute("ALTER TABLE users MODIFY id BIGINT UNSIGNED NOT NULL")
