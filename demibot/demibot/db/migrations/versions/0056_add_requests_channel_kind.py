"""add requests channel kind

Revision ID: 0056_add_requests_channel_kind
Revises: 0055_add_syncshell_transfer_budgets
Create Date: 2024-03-11
"""

from alembic import op


revision = "0056_add_requests_channel_kind"
down_revision = "0055_add_syncshell_transfer_budgets"
branch_labels = None
depends_on = None


def upgrade() -> None:
    bind = op.get_bind()
    dialect = bind.dialect.name

    if dialect == "postgresql":
        op.execute("ALTER TYPE channelkind ADD VALUE IF NOT EXISTS 'requests'")
    elif dialect == "mysql":
        op.execute(
            """
            ALTER TABLE guild_channels
            MODIFY COLUMN kind ENUM(
                'chat',
                'event',
                'requests',
                'fc_chat',
                'officer_chat',
                'officer_visible'
            ) NOT NULL
            """
        )
    else:
        # SQLite stores enums as plain TEXT so no migration is required.
        return


def downgrade() -> None:
    bind = op.get_bind()
    dialect = bind.dialect.name

    op.execute("DELETE FROM guild_channels WHERE kind = 'requests'")

    if dialect == "postgresql":
        op.execute(
            """
            CREATE TYPE channelkind_old AS ENUM(
                'chat',
                'event',
                'fc_chat',
                'officer_chat',
                'officer_visible'
            )
            """
        )
        op.execute(
            """
            ALTER TABLE guild_channels
            ALTER COLUMN kind TYPE channelkind_old
            USING kind::text::channelkind_old
            """
        )
        op.execute("DROP TYPE channelkind")
        op.execute("ALTER TYPE channelkind_old RENAME TO channelkind")
    elif dialect == "mysql":
        op.execute(
            """
            ALTER TABLE guild_channels
            MODIFY COLUMN kind ENUM(
                'chat',
                'event',
                'fc_chat',
                'officer_chat',
                'officer_visible'
            ) NOT NULL
            """
        )
    else:
        return
