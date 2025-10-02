"""add unicode emojis table"""

import json
import logging
from pathlib import Path

from alembic import op
import sqlalchemy as sa
from sqlalchemy.sql import table, column

# revision identifiers, used by Alembic.
revision = "0041_add_unicode_emojis_table"
down_revision = "0040_store_request_type_values"
branch_labels = None
depends_on = None


def _get_emoji_table() -> sa.Table:
    return sa.Table(
        "unicode_emojis",
        sa.MetaData(),
        sa.Column("emoji", sa.String(length=16), primary_key=True),
        sa.Column("name", sa.String(length=255), nullable=False),
        sa.Column("image_url", sa.String(length=255), nullable=False),
    )


def _load_unicode_emoji_data() -> list[dict]:
    base_path = Path(__file__).resolve()
    candidate_paths = [
        base_path.parents[2] / "data" / "unicode_emojis.json",
        base_path.parents[3] / "data" / "unicode_emojis.json",
    ]

    for data_path in candidate_paths:
        if data_path.exists():
            break
    else:
        raise FileNotFoundError(
            "Unable to locate unicode_emojis.json. Checked: "
            + ", ".join(str(path) for path in candidate_paths)
        )

    with data_path.open("r", encoding="utf-8") as f:
        try:
            return json.load(f)
        except json.JSONDecodeError as exc:
            logging.getLogger(__name__).exception(
                "Failed to parse unicode emoji dataset at %s", data_path
            )
            raise exc


def upgrade() -> None:
    bind = op.get_bind()
    inspector = sa.inspect(bind)
    table_exists = inspector.has_table("unicode_emojis")

    if not table_exists:
        op.create_table(
            "unicode_emojis",
            sa.Column("emoji", sa.String(length=16), primary_key=True),
            sa.Column("name", sa.String(length=255), nullable=False),
            sa.Column("image_url", sa.String(length=255), nullable=False),
        )

    emoji_table = _get_emoji_table()
    data = _load_unicode_emoji_data()

    existing_emojis = set()
    if table_exists:
        result = bind.execute(sa.select(emoji_table.c.emoji))
        existing_emojis = {row[0] for row in result}

    rows_to_insert = [
        {
            "emoji": e.get("emoji"),
            "name": e.get("name"),
            "image_url": e.get("imageUrl"),
        }
        for e in data
        if e.get("emoji") and e.get("emoji") not in existing_emojis
    ]

    if rows_to_insert:
        op.bulk_insert(
            table(
                "unicode_emojis",
                column("emoji", sa.String(length=16)),
                column("name", sa.String(length=255)),
                column("image_url", sa.String(length=255)),
            ),
            rows_to_insert,
        )


def downgrade() -> None:
    bind = op.get_bind()
    inspector = sa.inspect(bind)

    if not inspector.has_table("unicode_emojis"):
        return

    columns = inspector.get_columns("unicode_emojis")
    expected_columns = {"emoji", "name", "image_url"}
    column_names = {column["name"] for column in columns}

    if column_names != expected_columns:
        logging.getLogger(__name__).info(
            "Skipping drop of unicode_emojis; table schema does not match migration-created version."
        )
        return

    emoji_table = _get_emoji_table()

    try:
        data = _load_unicode_emoji_data()
    except FileNotFoundError:
        logging.getLogger(__name__).warning(
            "Skipping drop of unicode_emojis; unable to locate unicode_emojis.json for verification."
        )
        return

    dataset_emojis = {
        entry.get("emoji") for entry in data if entry.get("emoji")
    }

    existing_emojis = {
        row[0] for row in bind.execute(sa.select(emoji_table.c.emoji))
    }

    if existing_emojis.issubset(dataset_emojis):
        op.drop_table("unicode_emojis")
    else:
        logging.getLogger(__name__).info(
            "Skipping drop of unicode_emojis; table contains emojis outside the dataset and may predate this migration."
        )
