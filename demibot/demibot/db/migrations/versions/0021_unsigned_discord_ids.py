from __future__ import annotations

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import mysql
from sqlalchemy import inspect

# revision identifiers, used by Alembic.
revision = "0021_unsigned_discord_ids"
down_revision = "0020_align_install_status_enum"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.drop_index("ix_guilds_discord_guild_id", table_name="guilds")
    op.drop_index("ix_users_discord_user_id", table_name="users")
    op.drop_index("ix_messages_channel_id_created_at", table_name="messages")
    op.drop_index("ix_embeds_channel_id", table_name="embeds")
    op.drop_index(op.f("ix_roles_discord_role_id"), table_name="roles")

    op.alter_column(
        "guilds",
        "discord_guild_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
    )
    op.alter_column(
        "guild_config",
        "officer_visible_channel_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=True,
    )
    op.alter_column(
        "guild_config",
        "officer_role_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=True,
    )
    op.alter_column(
        "guild_channels",
        "channel_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
    )
    op.alter_column(
        "users",
        "discord_user_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
    )
    op.alter_column(
        "roles",
        "discord_role_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
    )
    op.alter_column(
        "messages",
        "discord_message_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
    )
    op.alter_column(
        "messages",
        "channel_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
    )
    op.alter_column(
        "embeds",
        "discord_message_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
    )
    op.alter_column(
        "embeds",
        "channel_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
    )
    op.alter_column(
        "attendance",
        "discord_message_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
    )
    op.alter_column(
        "event_buttons",
        "message_id",
        existing_type=sa.BigInteger(),
        type_=mysql.BIGINT(unsigned=True),
        existing_nullable=False,
    )

    bind = op.get_bind()
    insp = inspect(bind)
    if "recurring_events" in insp.get_table_names():
        op.alter_column(
            "recurring_events",
            "id",
            existing_type=sa.BigInteger(),
            type_=mysql.BIGINT(unsigned=True),
            existing_nullable=False,
        )
        op.alter_column(
            "recurring_events",
            "channel_id",
            existing_type=sa.BigInteger(),
            type_=mysql.BIGINT(unsigned=True),
            existing_nullable=False,
        )

    op.create_index(
        "ix_guilds_discord_guild_id", "guilds", ["discord_guild_id"], unique=True
    )
    op.create_index(
        "ix_users_discord_user_id", "users", ["discord_user_id"], unique=True
    )
    op.create_index(
        "ix_messages_channel_id_created_at",
        "messages",
        ["channel_id", "created_at"],
    )
    op.create_index("ix_embeds_channel_id", "embeds", ["channel_id"])
    op.create_index(
        op.f("ix_roles_discord_role_id"),
        "roles",
        ["discord_role_id"],
        unique=True,
    )


def downgrade() -> None:
    op.drop_index(op.f("ix_roles_discord_role_id"), table_name="roles")
    op.drop_index("ix_embeds_channel_id", table_name="embeds")
    op.drop_index("ix_messages_channel_id_created_at", table_name="messages")
    op.drop_index("ix_users_discord_user_id", table_name="users")
    op.drop_index("ix_guilds_discord_guild_id", table_name="guilds")

    bind = op.get_bind()
    insp = inspect(bind)
    if "recurring_events" in insp.get_table_names():
        op.alter_column(
            "recurring_events",
            "channel_id",
            existing_type=mysql.BIGINT(unsigned=True),
            type_=sa.BigInteger(),
            existing_nullable=False,
        )
        op.alter_column(
            "recurring_events",
            "id",
            existing_type=mysql.BIGINT(unsigned=True),
            type_=sa.BigInteger(),
            existing_nullable=False,
        )

    op.alter_column(
        "event_buttons",
        "message_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
    )
    op.alter_column(
        "attendance",
        "discord_message_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
    )
    op.alter_column(
        "embeds",
        "channel_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
    )
    op.alter_column(
        "embeds",
        "discord_message_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
    )
    op.alter_column(
        "messages",
        "channel_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
    )
    op.alter_column(
        "messages",
        "discord_message_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
    )
    op.alter_column(
        "roles",
        "discord_role_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
    )
    op.alter_column(
        "users",
        "discord_user_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
    )
    op.alter_column(
        "guild_channels",
        "channel_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
    )
    op.alter_column(
        "guild_config",
        "officer_role_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=True,
    )
    op.alter_column(
        "guild_config",
        "officer_visible_channel_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=True,
    )
    op.alter_column(
        "guilds",
        "discord_guild_id",
        existing_type=mysql.BIGINT(unsigned=True),
        type_=sa.BigInteger(),
        existing_nullable=False,
    )

    op.create_index(
        op.f("ix_roles_discord_role_id"),
        "roles",
        ["discord_role_id"],
        unique=True,
    )
    op.create_index("ix_embeds_channel_id", "embeds", ["channel_id"])
    op.create_index(
        "ix_messages_channel_id_created_at",
        "messages",
        ["channel_id", "created_at"],
    )
    op.create_index(
        "ix_users_discord_user_id", "users", ["discord_user_id"], unique=True
    )
    op.create_index(
        "ix_guilds_discord_guild_id", "guilds", ["discord_guild_id"], unique=True
    )
