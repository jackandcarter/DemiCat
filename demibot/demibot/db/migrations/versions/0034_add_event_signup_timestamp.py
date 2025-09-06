import sqlalchemy as sa
from alembic import op
from datetime import datetime

# revision identifiers, used by Alembic.
revision = "0034_add_event_signup_timestamp"
down_revision = "0033_add_event_templates"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column(
        "event_signups",
        sa.Column("created_at", sa.DateTime(), nullable=True),
    )
    op.create_index(
        "ix_event_signups_discord_message_id_choice",
        "event_signups",
        ["message_id", "tag"],
    )
    conn = op.get_bind()
    conn.execute(
        sa.text("UPDATE event_signups SET created_at = :now WHERE created_at IS NULL"),
        {"now": datetime.utcnow()},
    )
    op.alter_column("event_signups", "created_at", nullable=False)


def downgrade() -> None:
    op.drop_index(
        "ix_event_signups_discord_message_id_choice",
        table_name="event_signups",
    )
    op.drop_column("event_signups", "created_at")
