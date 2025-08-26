"""add requests tables

Revision ID: 0011_add_requests_tables
Revises: 0010_add_buttons_json_to_embeds
Create Date: 2024-05-29
"""

from __future__ import annotations

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects.mysql import BIGINT

# revision identifiers, used by Alembic.
revision = "0011_add_requests_tables"
down_revision = "0010_add_buttons_json_to_embeds"
branch_labels = None
depends_on = None


request_type = sa.Enum("item", "run", "event", name="request_type")
request_status = sa.Enum("open", "approved", "denied", name="request_status")
urgency = sa.Enum("low", "medium", "high", name="urgency")


def upgrade() -> None:
    bind = op.get_bind()
    request_type.create(bind, checkfirst=True)
    request_status.create(bind, checkfirst=True)
    urgency.create(bind, checkfirst=True)

    op.create_table(
        "requests",
        sa.Column("id", sa.Integer(), nullable=False, primary_key=True),
        sa.Column("guild_id", sa.Integer(), sa.ForeignKey("guilds.id"), nullable=False),
        sa.Column(
            "user_id",
            BIGINT(unsigned=True),
            sa.ForeignKey("users.id"),
            nullable=False,
        ),
        sa.Column("title", sa.String(length=255), nullable=False),
        sa.Column("description", sa.Text()),
        sa.Column("type", request_type, nullable=False),
        sa.Column("status", request_status, nullable=False),
        sa.Column("urgency", urgency, nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.Column("version", sa.Integer(), nullable=False, server_default="1"),
    )
    op.create_index("ix_requests_type", "requests", ["type"])
    op.create_index("ix_requests_status", "requests", ["status"])
    op.create_index("ix_requests_urgency", "requests", ["urgency"])
    op.create_index(
        "ix_requests_text",
        "requests",
        ["title", "description"],
        mysql_prefix="FULLTEXT",
    )

    op.create_table(
        "request_items",
        sa.Column("id", sa.Integer(), nullable=False, primary_key=True),
        sa.Column(
            "request_id",
            sa.Integer(),
            sa.ForeignKey("requests.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column("item_id", sa.BigInteger(), nullable=False),
        sa.Column("quantity", sa.Integer(), nullable=False, server_default="1"),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.Column("version", sa.Integer(), nullable=False, server_default="1"),
    )
    op.create_index("ix_request_items_request_id", "request_items", ["request_id"])

    op.create_table(
        "request_runs",
        sa.Column("id", sa.Integer(), nullable=False, primary_key=True),
        sa.Column(
            "request_id",
            sa.Integer(),
            sa.ForeignKey("requests.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column("run_id", sa.BigInteger(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.Column("version", sa.Integer(), nullable=False, server_default="1"),
    )
    op.create_index("ix_request_runs_request_id", "request_runs", ["request_id"])

    op.create_table(
        "request_events",
        sa.Column("id", sa.Integer(), nullable=False, primary_key=True),
        sa.Column(
            "request_id",
            sa.Integer(),
            sa.ForeignKey("requests.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column("event_id", sa.BigInteger(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.Column("version", sa.Integer(), nullable=False, server_default="1"),
    )
    op.create_index("ix_request_events_request_id", "request_events", ["request_id"])


def downgrade() -> None:
    op.drop_index("ix_request_events_request_id", table_name="request_events")
    op.drop_table("request_events")
    op.drop_index("ix_request_runs_request_id", table_name="request_runs")
    op.drop_table("request_runs")
    op.drop_index("ix_request_items_request_id", table_name="request_items")
    op.drop_table("request_items")

    op.drop_index("ix_requests_text", table_name="requests")
    op.drop_index("ix_requests_urgency", table_name="requests")
    op.drop_index("ix_requests_status", table_name="requests")
    op.drop_index("ix_requests_type", table_name="requests")
    op.drop_table("requests")

    bind = op.get_bind()
    urgency.drop(bind, checkfirst=True)
    request_status.drop(bind, checkfirst=True)
    request_type.drop(bind, checkfirst=True)

