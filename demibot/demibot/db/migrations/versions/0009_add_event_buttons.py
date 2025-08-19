import json
import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision = "0009_add_event_buttons"
down_revision = "0008_add_signup_presets"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "event_buttons",
        sa.Column("message_id", sa.BigInteger(), nullable=False),
        sa.Column("tag", sa.String(length=50), nullable=False),
        sa.Column("label", sa.String(length=255), nullable=False),
        sa.Column("emoji", sa.String(length=64), nullable=True),
        sa.Column("style", sa.Integer(), nullable=True),
        sa.Column("max_signups", sa.Integer(), nullable=True),
        sa.PrimaryKeyConstraint("message_id", "tag"),
    )

    conn = op.get_bind()
    rows = conn.execute(
        sa.text("SELECT discord_message_id, payload_json FROM embeds")
    ).fetchall()
    for message_id, payload_json in rows:
        try:
            payload = json.loads(payload_json)
        except Exception:
            continue
        for b in payload.get("buttons", []):
            cid = b.get("customId") or ""
            if cid.startswith("rsvp:"):
                tag = cid.split(":", 1)[1]
                conn.execute(
                    sa.text(
                        "INSERT INTO event_buttons (message_id, tag, label, emoji, style, max_signups) "
                        "VALUES (:message_id, :tag, :label, :emoji, :style, :max_signups)"
                    ),
                    {
                        "message_id": message_id,
                        "tag": tag,
                        "label": b.get("label"),
                        "emoji": b.get("emoji"),
                        "style": b.get("style"),
                        "max_signups": b.get("maxSignups"),
                    },
                )


def downgrade() -> None:
    op.drop_table("event_buttons")
