"""add fc and asset tables

Revision ID: 0013_add_fc_and_asset_tables
Revises: 0012_add_request_assignee_and_hq
Create Date: 2025-08-21
"""

from __future__ import annotations

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision = "0013_add_fc_and_asset_tables"
down_revision = "0012_add_request_assignee_and_hq"
branch_labels = None
depends_on = None

asset_kind = sa.Enum("appearance", "file", "script", name="asset_kind")
install_status = sa.Enum("pending", "installed", "failed", name="install_status")


def upgrade() -> None:
    bind = op.get_bind()
    asset_kind.create(bind, checkfirst=True)
    install_status.create(bind, checkfirst=True)

    op.create_table(
        "fc",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("name", sa.String(length=255), nullable=False),
        sa.Column("world", sa.String(length=32), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
    )

    op.create_table(
        "fc_user",
        sa.Column(
            "fc_id",
            sa.Integer(),
            sa.ForeignKey("fc.id", ondelete="CASCADE"),
            primary_key=True,
        ),
        sa.Column(
            "user_id",
            sa.Integer(),
            sa.ForeignKey("users.id", ondelete="CASCADE"),
            primary_key=True,
        ),
        sa.Column("joined_at", sa.DateTime(), nullable=False),
    )

    op.create_table(
        "asset",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column(
            "fc_id",
            sa.Integer(),
            sa.ForeignKey("fc.id", ondelete="CASCADE"),
            nullable=True,
        ),
        sa.Column("kind", asset_kind, nullable=False),
        sa.Column("name", sa.String(length=255), nullable=False),
        sa.Column("hash", sa.String(length=64), nullable=False),
        sa.Column("size", sa.Integer()),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.Column("version", sa.Integer(), nullable=False, server_default="1"),
    )
    op.create_index("ix_asset_fc_id", "asset", ["fc_id"])
    op.create_index("ix_asset_kind", "asset", ["kind"])
    op.create_index("ix_asset_hash", "asset", ["hash"], unique=True)

    op.create_table(
        "asset_dependency",
        sa.Column(
            "asset_id",
            sa.Integer(),
            sa.ForeignKey("asset.id", ondelete="CASCADE"),
            primary_key=True,
        ),
        sa.Column(
            "dependency_id",
            sa.Integer(),
            sa.ForeignKey("asset.id", ondelete="CASCADE"),
            primary_key=True,
        ),
    )

    op.create_table(
        "appearance_bundle",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column(
            "fc_id",
            sa.Integer(),
            sa.ForeignKey("fc.id", ondelete="CASCADE"),
            nullable=True,
        ),
        sa.Column("name", sa.String(length=255), nullable=False),
        sa.Column("description", sa.Text()),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
    )

    op.create_table(
        "appearance_bundle_item",
        sa.Column(
            "bundle_id",
            sa.Integer(),
            sa.ForeignKey("appearance_bundle.id", ondelete="CASCADE"),
            primary_key=True,
        ),
        sa.Column(
            "asset_id",
            sa.Integer(),
            sa.ForeignKey("asset.id", ondelete="CASCADE"),
            primary_key=True,
        ),
        sa.Column("quantity", sa.Integer(), nullable=False, server_default="1"),
    )

    op.create_table(
        "user_installation",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column(
            "user_id",
            sa.Integer(),
            sa.ForeignKey("users.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column(
            "asset_id",
            sa.Integer(),
            sa.ForeignKey("asset.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column("status", install_status, nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.Column("version", sa.Integer(), nullable=False, server_default="1"),
    )
    op.create_index("ix_user_installation_user_id", "user_installation", ["user_id"])
    op.create_index("ix_user_installation_asset_id", "user_installation", ["asset_id"])
    op.create_index("ix_user_installation_status", "user_installation", ["status"])

    op.create_table(
        "index_checkpoint",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("kind", asset_kind, nullable=False),
        sa.Column("last_id", sa.Integer(), nullable=False),
        sa.Column("last_generated_at", sa.DateTime(), nullable=False),
    )
    op.create_index(
        "ix_index_checkpoint_kind", "index_checkpoint", ["kind"], unique=True
    )


def downgrade() -> None:
    op.drop_index("ix_index_checkpoint_kind", table_name="index_checkpoint")
    op.drop_table("index_checkpoint")

    op.drop_index("ix_user_installation_status", table_name="user_installation")
    op.drop_index("ix_user_installation_asset_id", table_name="user_installation")
    op.drop_index("ix_user_installation_user_id", table_name="user_installation")
    op.drop_table("user_installation")

    op.drop_table("appearance_bundle_item")
    op.drop_table("appearance_bundle")
    op.drop_table("asset_dependency")

    op.drop_index("ix_asset_hash", table_name="asset")
    op.drop_index("ix_asset_kind", table_name="asset")
    op.drop_index("ix_asset_fc_id", table_name="asset")
    op.drop_table("asset")

    op.drop_table("fc_user")
    op.drop_table("fc")

    bind = op.get_bind()
    install_status.drop(bind, checkfirst=True)
    asset_kind.drop(bind, checkfirst=True)
