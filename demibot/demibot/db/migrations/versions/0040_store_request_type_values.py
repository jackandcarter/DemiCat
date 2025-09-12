"""store request type values

Revision ID: 0040_store_request_type_values
Revises: 0039_add_officer_role_ids
Create Date: 2025-02-16
"""

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import mysql


revision = "0040_store_request_type_values"
down_revision = "0039_add_officer_role_ids"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.alter_column(
        "requests",
        "type",
        existing_type=mysql.ENUM("ITEM", "RUN", "EVENT", name="request_type"),
        type_=mysql.ENUM("item", "run", "event", name="request_type"),
        nullable=False,
    )


def downgrade() -> None:
    op.alter_column(
        "requests",
        "type",
        existing_type=mysql.ENUM("item", "run", "event", name="request_type"),
        type_=mysql.ENUM("ITEM", "RUN", "EVENT", name="request_type"),
        nullable=False,
    )

