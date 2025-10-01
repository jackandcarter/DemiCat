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


def upgrade() -> None:
    op.create_table(
        "unicode_emojis",
        sa.Column("emoji", sa.String(length=16), primary_key=True),
        sa.Column("name", sa.String(length=255), nullable=False),
        sa.Column("image_url", sa.String(length=255), nullable=False),
    )

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
            data = json.load(f)
        except json.JSONDecodeError as exc:
            logging.getLogger(__name__).exception(
                "Failed to parse unicode emoji dataset at %s", data_path
            )
            raise exc

    emoji_table = table(
        "unicode_emojis",
        column("emoji", sa.String),
        column("name", sa.String),
        column("image_url", sa.String),
    )
    op.bulk_insert(
        emoji_table,
        [
            {"emoji": e.get("emoji"), "name": e.get("name"), "image_url": e.get("imageUrl")}
            for e in data
        ],
    )


def downgrade() -> None:
    op.drop_table("unicode_emojis")
