"""add unicode emojis table"""

from pathlib import Path
import json

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

    data_path = Path(__file__).resolve().parents[2] / "data" / "unicode_emojis.json"
    try:
        with data_path.open("r", encoding="utf-8") as f:
            data = json.load(f)
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
    except Exception:
        pass


def downgrade() -> None:
    op.drop_table("unicode_emojis")
