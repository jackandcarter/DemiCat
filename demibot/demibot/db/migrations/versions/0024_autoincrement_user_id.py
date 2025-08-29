from __future__ import annotations

from alembic import op

# revision identifiers, used by Alembic.
revision = "0024_autoincrement_user_id"
down_revision = "0023_add_recurring_events"
branch_labels = None
depends_on = None


def upgrade() -> None:
    # drop FKs referencing users.id
    op.drop_constraint("user_keys_ibfk_1", "user_keys", type_="foreignkey")
    op.drop_constraint("memberships_ibfk_2", "memberships", type_="foreignkey")
    op.drop_constraint("messages_ibfk_2", "messages", type_="foreignkey")
    op.drop_constraint("attendance_ibfk_1", "attendance", type_="foreignkey")
    op.drop_constraint("requests_ibfk_2", "requests", type_="foreignkey")
    op.drop_constraint("requests_ibfk_3", "requests", type_="foreignkey")
    op.drop_constraint("fc_user_ibfk_2", "fc_user", type_="foreignkey")
    op.drop_constraint("asset_ibfk_2", "asset", type_="foreignkey")
    op.drop_constraint(
        "user_installation_ibfk_1", "user_installation", type_="foreignkey"
    )

    op.execute(
        "ALTER TABLE users MODIFY id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT"
    )

    # recreate FKs
    op.create_foreign_key(
        "user_keys_ibfk_1", "user_keys", "users", ["user_id"], ["id"]
    )
    op.create_foreign_key(
        "memberships_ibfk_2", "memberships", "users", ["user_id"], ["id"]
    )
    op.create_foreign_key(
        "messages_ibfk_2", "messages", "users", ["author_id"], ["id"]
    )
    op.create_foreign_key(
        "attendance_ibfk_1", "attendance", "users", ["user_id"], ["id"]
    )
    op.create_foreign_key(
        "requests_ibfk_2", "requests", "users", ["user_id"], ["id"]
    )
    op.create_foreign_key(
        "requests_ibfk_3", "requests", "users", ["assignee_id"], ["id"]
    )
    op.create_foreign_key(
        "fc_user_ibfk_2",
        "fc_user",
        "users",
        ["user_id"],
        ["id"],
        ondelete="CASCADE",
    )
    op.create_foreign_key(
        "asset_ibfk_2",
        "asset",
        "users",
        ["uploader_id"],
        ["id"],
        ondelete="SET NULL",
    )
    op.create_foreign_key(
        "user_installation_ibfk_1",
        "user_installation",
        "users",
        ["user_id"],
        ["id"],
        ondelete="CASCADE",
    )


def downgrade() -> None:
    # drop FKs referencing users.id
    op.drop_constraint("user_keys_ibfk_1", "user_keys", type_="foreignkey")
    op.drop_constraint("memberships_ibfk_2", "memberships", type_="foreignkey")
    op.drop_constraint("messages_ibfk_2", "messages", type_="foreignkey")
    op.drop_constraint("attendance_ibfk_1", "attendance", type_="foreignkey")
    op.drop_constraint("requests_ibfk_2", "requests", type_="foreignkey")
    op.drop_constraint("requests_ibfk_3", "requests", type_="foreignkey")
    op.drop_constraint("fc_user_ibfk_2", "fc_user", type_="foreignkey")
    op.drop_constraint("asset_ibfk_2", "asset", type_="foreignkey")
    op.drop_constraint(
        "user_installation_ibfk_1", "user_installation", type_="foreignkey"
    )

    op.execute("ALTER TABLE users MODIFY id BIGINT UNSIGNED NOT NULL")

    # recreate FKs
    op.create_foreign_key(
        "user_keys_ibfk_1", "user_keys", "users", ["user_id"], ["id"]
    )
    op.create_foreign_key(
        "memberships_ibfk_2", "memberships", "users", ["user_id"], ["id"]
    )
    op.create_foreign_key(
        "messages_ibfk_2", "messages", "users", ["author_id"], ["id"]
    )
    op.create_foreign_key(
        "attendance_ibfk_1", "attendance", "users", ["user_id"], ["id"]
    )
    op.create_foreign_key(
        "requests_ibfk_2", "requests", "users", ["user_id"], ["id"]
    )
    op.create_foreign_key(
        "requests_ibfk_3", "requests", "users", ["assignee_id"], ["id"]
    )
    op.create_foreign_key(
        "fc_user_ibfk_2",
        "fc_user",
        "users",
        ["user_id"],
        ["id"],
        ondelete="CASCADE",
    )
    op.create_foreign_key(
        "asset_ibfk_2",
        "asset",
        "users",
        ["uploader_id"],
        ["id"],
        ondelete="SET NULL",
    )
    op.create_foreign_key(
        "user_installation_ibfk_1",
        "user_installation",
        "users",
        ["user_id"],
        ["id"],
        ondelete="CASCADE",
    )
