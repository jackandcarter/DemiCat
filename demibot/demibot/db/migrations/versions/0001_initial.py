from __future__ import annotations

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision = "0001_initial"
down_revision = None
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "guilds",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("discord_guild_id", sa.BigInteger(), nullable=False),
        sa.Column("name", sa.String(length=255), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
    )
    op.create_index(
        "ix_guilds_discord_guild_id", "guilds", ["discord_guild_id"], unique=True
    )

    op.create_table(
        "guild_config",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("guild_id", sa.Integer(), sa.ForeignKey("guilds.id")),
        sa.Column("event_channel_id", sa.BigInteger()),
        sa.Column("fc_chat_channel_id", sa.BigInteger()),
        sa.Column("officer_chat_channel_id", sa.BigInteger()),
        sa.Column("officer_visible_channel_id", sa.BigInteger()),
        sa.Column("officer_role_id", sa.BigInteger()),
        sa.Column("chat_role_id", sa.BigInteger()),
    )

    op.create_table(
        "guild_channels",
        sa.Column("guild_id", sa.Integer(), sa.ForeignKey("guilds.id"), primary_key=True),
        sa.Column("channel_id", sa.BigInteger(), primary_key=True),
        sa.Column(
            "kind",
            sa.Enum(
                "chat",
                "event",
                "fc_chat",
                "officer_chat",
                "officer_visible",
                name="channelkind",
            ),
            primary_key=True,
        ),
    )

    op.create_table(
        "users",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("discord_user_id", sa.BigInteger(), nullable=False),
        sa.Column("global_name", sa.String(length=255)),
        sa.Column("discriminator", sa.String(length=10)),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
    )
    op.create_index(
        "ix_users_discord_user_id", "users", ["discord_user_id"], unique=True
    )

    op.create_table(
        "user_keys",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("user_id", sa.Integer(), sa.ForeignKey("users.id"), nullable=False),
        sa.Column("guild_id", sa.Integer(), sa.ForeignKey("guilds.id"), nullable=False),
        sa.Column("token", sa.String(length=64), nullable=False),
        sa.Column("enabled", sa.Boolean(), nullable=False, server_default=sa.text("1")),
        sa.Column("roles_cached", sa.Text()),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.Column("last_used_at", sa.DateTime()),
    )
    op.create_index("ix_user_keys_token", "user_keys", ["token"], unique=True)

    op.create_table(
        "memberships",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("guild_id", sa.Integer(), sa.ForeignKey("guilds.id"), nullable=False),
        sa.Column("user_id", sa.Integer(), sa.ForeignKey("users.id"), nullable=False),
    )

    op.create_table(
        "roles",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("guild_id", sa.Integer(), sa.ForeignKey("guilds.id"), nullable=False),
        sa.Column("name", sa.String(length=255), nullable=False),
        sa.Column("is_officer", sa.Boolean(), nullable=False, server_default=sa.text("0")),
        sa.Column("is_chat", sa.Boolean(), nullable=False, server_default=sa.text("0")),
    )

    op.create_table(
        "membership_roles",
        sa.Column(
            "membership_id",
            sa.Integer(),
            sa.ForeignKey("memberships.id"),
            primary_key=True,
        ),
        sa.Column("role_id", sa.Integer(), sa.ForeignKey("roles.id"), primary_key=True),
    )

    op.create_table(
        "messages",
        sa.Column("discord_message_id", sa.BigInteger(), primary_key=True),
        sa.Column("channel_id", sa.BigInteger(), nullable=False),
        sa.Column("guild_id", sa.Integer(), sa.ForeignKey("guilds.id"), nullable=False),
        sa.Column("author_id", sa.Integer(), sa.ForeignKey("users.id"), nullable=False),
        sa.Column("author_name", sa.String(length=255), nullable=False),
        sa.Column("content_raw", sa.Text(), nullable=False),
        sa.Column("content_display", sa.Text(), nullable=False),
        sa.Column("is_officer", sa.Boolean(), nullable=False, server_default=sa.text("0")),
        sa.Column("created_at", sa.DateTime(), nullable=False),
    )
    op.create_index(
        "ix_messages_channel_id_created_at",
        "messages",
        ["channel_id", "created_at"],
    )

    op.create_table(
        "embeds",
        sa.Column("discord_message_id", sa.BigInteger(), primary_key=True),
        sa.Column("channel_id", sa.BigInteger(), nullable=False),
        sa.Column("guild_id", sa.Integer(), sa.ForeignKey("guilds.id"), nullable=False),
        sa.Column("payload_json", sa.Text(), nullable=False),
        sa.Column("source", sa.String(length=16), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
    )
    op.create_index("ix_embeds_channel_id", "embeds", ["channel_id"])

    op.create_table(
        "attendance",
        sa.Column("discord_message_id", sa.BigInteger(), primary_key=True),
        sa.Column("user_id", sa.Integer(), sa.ForeignKey("users.id"), primary_key=True),
        sa.Column("choice", sa.String(length=10), nullable=False),
    )


def downgrade() -> None:
    op.drop_table("attendance")
    op.drop_index("ix_embeds_channel_id", table_name="embeds")
    op.drop_table("embeds")
    op.drop_index("ix_messages_channel_id_created_at", table_name="messages")
    op.drop_table("messages")
    op.drop_table("membership_roles")
    op.drop_table("roles")
    op.drop_table("memberships")
    op.drop_index("ix_user_keys_token", table_name="user_keys")
    op.drop_table("user_keys")
    op.drop_index("ix_users_discord_user_id", table_name="users")
    op.drop_table("users")
    op.drop_table("guild_channels")
    op.drop_table("guild_config")
    op.drop_index("ix_guilds_discord_guild_id", table_name="guilds")
    op.drop_table("guilds")
