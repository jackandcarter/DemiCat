import json
import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision = "0010_add_buttons_json_to_embeds"
down_revision = "0009_add_event_buttons"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column("embeds", sa.Column("buttons_json", sa.Text(), nullable=True))
    conn = op.get_bind()
    rows = conn.execute(
        sa.text("SELECT discord_message_id, payload_json FROM embeds")
    ).fetchall()
    for message_id, payload_json in rows:
        try:
            payload = json.loads(payload_json)
        except Exception:
            continue
        buttons = payload.get("buttons")
        if buttons:
            conn.execute(
                sa.text(
                    "UPDATE embeds SET buttons_json = :bj WHERE discord_message_id = :mid"
                ),
                {"bj": json.dumps(buttons), "mid": message_id},
            )


def downgrade() -> None:
    op.drop_column("embeds", "buttons_json")
