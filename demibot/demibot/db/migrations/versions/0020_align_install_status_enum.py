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
    """Upgrade install status values to new enum."""

    # First convert the enum to a plain string column so values can be updated
    op.alter_column(
        "user_installation",
        "status",
        existing_type=mysql.ENUM(
            "pending", "installed", "failed", name="install_status"
        ),
        type_=sa.String(length=20),
        existing_nullable=False,
    )

    # Map legacy values to the new uppercase forms
    op.execute(
        "UPDATE user_installation SET status='DOWNLOADED' WHERE status='pending'"
    )
    op.execute(
        "UPDATE user_installation SET status='INSTALLED' WHERE status='installed'"
    )
    op.execute(
        "UPDATE user_installation SET status='FAILED' WHERE status='failed'"
    )

    # Finally convert the column back to an enum with only the new values
    op.alter_column(
        "user_installation",
        "status",
        existing_type=sa.String(length=20),
        type_=mysql.ENUM(
            "DOWNLOADED", "INSTALLED", "APPLIED", "FAILED", name="install_status"
        ),
        nullable=False,
    )


def downgrade() -> None:
    """Revert install status enum to legacy values."""

    # Convert enum to string so values can be mapped back
    op.alter_column(
        "user_installation",
        "status",
        existing_type=mysql.ENUM(
            "DOWNLOADED", "INSTALLED", "APPLIED", "FAILED", name="install_status"
        ),
        type_=sa.String(length=20),
        existing_nullable=False,
    )

    # Map current values to the legacy lowercase variants
    op.execute(
        "UPDATE user_installation SET status='pending' WHERE status='DOWNLOADED'"
    )
    op.execute(
        "UPDATE user_installation SET status='installed' WHERE status IN ('INSTALLED', 'APPLIED')"
    )
    op.execute(
        "UPDATE user_installation SET status='failed' WHERE status='FAILED'"
    )

    # Restore the original enum type
    op.alter_column(
        "user_installation",
        "status",
        existing_type=sa.String(length=20),
        type_=mysql.ENUM(
            "pending", "installed", "failed", name="install_status"
        ),
        nullable=False,
    )
