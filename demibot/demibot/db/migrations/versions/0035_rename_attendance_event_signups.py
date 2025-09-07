import sqlalchemy as sa
from alembic import op
from datetime import datetime

# revision identifiers, used by Alembic.
revision = "0035_rename_attendance_event_signups"
down_revision = "0033_add_event_templates"
branch_labels = None
depends_on = None


def upgrade() -> None:
    bind = op.get_bind()
    insp = sa.inspect(bind)

    if "attendance" in insp.get_table_names():
        op.rename_table("attendance", "attendance_old")

        op.create_table(
            "event_signups",
            sa.Column("id", sa.Integer(), primary_key=True),
            sa.Column("message_id", sa.BigInteger(), nullable=False),
            sa.Column("user_id", sa.BigInteger(), sa.ForeignKey("users.id"), nullable=False),
            sa.Column("tag", sa.String(length=50), nullable=False),
            sa.Column("created_at", sa.DateTime(), nullable=False),
            sa.UniqueConstraint("message_id", "user_id", name="uq_event_signups_message_user"),
        )
        op.create_index(
            "ix_event_signups_discord_message_id_choice",
            "event_signups",
            ["message_id", "tag"],
        )

        rows = bind.execute(
            sa.text(
                "SELECT discord_message_id AS message_id, user_id, choice AS tag FROM attendance_old"
            )
        ).fetchall()
        now = datetime.utcnow()
        if rows:
            bind.execute(
                sa.text(
                    "INSERT INTO event_signups (message_id, user_id, tag, created_at) VALUES (:message_id, :user_id, :tag, :created_at)"
                ),
                [
                    {
                        "message_id": r.message_id if hasattr(r, "message_id") else r[0],
                        "user_id": r.user_id if hasattr(r, "user_id") else r[1],
                        "tag": r.tag if hasattr(r, "tag") else r[2],
                        "created_at": now,
                    }
                    for r in rows
                ],
            )
        op.drop_table("attendance_old")
    else:
        op.create_table(
            "event_signups",
            sa.Column("id", sa.Integer(), primary_key=True),
            sa.Column("message_id", sa.BigInteger(), nullable=False),
            sa.Column("user_id", sa.BigInteger(), sa.ForeignKey("users.id"), nullable=False),
            sa.Column("tag", sa.String(length=50), nullable=False),
            sa.Column("created_at", sa.DateTime(), nullable=False),
            sa.UniqueConstraint("message_id", "user_id", name="uq_event_signups_message_user"),
        )
        op.create_index(
            "ix_event_signups_discord_message_id_choice",
            "event_signups",
            ["message_id", "tag"],
        )


def downgrade() -> None:
    bind = op.get_bind()
    insp = sa.inspect(bind)

    if "event_signups" in insp.get_table_names():
        op.rename_table("event_signups", "event_signups_old")

        op.create_table(
            "attendance",
            sa.Column("discord_message_id", sa.BigInteger(), nullable=False),
            sa.Column("user_id", sa.BigInteger(), sa.ForeignKey("users.id"), nullable=False),
            sa.Column("choice", sa.String(length=10), nullable=False),
            sa.PrimaryKeyConstraint("discord_message_id", "user_id"),
        )
        rows = bind.execute(
            sa.text(
                "SELECT message_id AS discord_message_id, user_id, tag AS choice FROM event_signups_old"
            )
        ).fetchall()
        if rows:
            bind.execute(
                sa.text(
                    "INSERT INTO attendance (discord_message_id, user_id, choice) VALUES (:discord_message_id, :user_id, :choice)"
                ),
                [
                    {
                        "discord_message_id": r.discord_message_id if hasattr(r, "discord_message_id") else r[0],
                        "user_id": r.user_id if hasattr(r, "user_id") else r[1],
                        "choice": r.choice if hasattr(r, "choice") else r[2],
                    }
                    for r in rows
                ],
            )
        op.drop_table("event_signups_old")
    else:
        op.drop_table("attendance")
