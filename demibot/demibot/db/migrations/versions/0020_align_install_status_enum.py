"""align install status enum with model

Revision ID: 0020_align_install_status_enum
Revises: 0019_expand_request_status
Create Date: 2025-11-26
"""

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import mysql

# revision identifiers, used by Alembic.
revision = "0020_align_install_status_enum"
down_revision = "0019_expand_request_status"
branch_labels = None
depends_on = None


def upgrade() -> None:
    # Allow both old and new enum values temporarily
    op.alter_column(
        "user_installation",
        "status",
        existing_type=mysql.ENUM("pending", "installed", "failed", name="install_status"),
        type_=mysql.ENUM(
            "pending",
            "installed",
            "failed",
            "DOWNLOADED",
            "INSTALLED",
            "APPLIED",
            "FAILED",
            name="install_status",
        ),
        nullable=False,
    )

    # Migrate existing data to new values
    op.execute(
        "UPDATE user_installation SET status='DOWNLOADED' WHERE status='pending'"
    )
    op.execute(
        "UPDATE user_installation SET status='INSTALLED' WHERE status='installed'"
    )
    op.execute(
        "UPDATE user_installation SET status='FAILED' WHERE status='failed'"
    )

    # Drop old enum values
    op.alter_column(
        "user_installation",
        "status",
        existing_type=mysql.ENUM(
            "pending",
            "installed",
            "failed",
            "DOWNLOADED",
            "INSTALLED",
            "APPLIED",
            "FAILED",
            name="install_status",
        ),
        type_=mysql.ENUM(
            "DOWNLOADED", "INSTALLED", "APPLIED", "FAILED", name="install_status"
        ),
        nullable=False,
    )


def downgrade() -> None:
    # Re-introduce old enum values to allow data migration
    op.alter_column(
        "user_installation",
        "status",
        existing_type=mysql.ENUM(
            "DOWNLOADED", "INSTALLED", "APPLIED", "FAILED", name="install_status"
        ),
        type_=mysql.ENUM(
            "pending",
            "installed",
            "failed",
            "DOWNLOADED",
            "INSTALLED",
            "APPLIED",
            "FAILED",
            name="install_status",
        ),
        nullable=False,
    )

    # Map data back to legacy values
    op.execute(
        "UPDATE user_installation SET status='pending' WHERE status='DOWNLOADED'"
    )
    op.execute(
        "UPDATE user_installation SET status='installed' WHERE status IN ('INSTALLED', 'APPLIED')"
    )
    op.execute(
        "UPDATE user_installation SET status='failed' WHERE status='FAILED'"
    )

    # Restore old enum definition
    op.alter_column(
        "user_installation",
        "status",
        existing_type=mysql.ENUM(
            "pending",
            "installed",
            "failed",
            "DOWNLOADED",
            "INSTALLED",
            "APPLIED",
            "FAILED",
            name="install_status",
        ),
        type_=mysql.ENUM("pending", "installed", "failed", name="install_status"),
        nullable=False,
    )
