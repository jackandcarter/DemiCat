from __future__ import annotations

from alembic import op

from .. import models

# revision identifiers, used by Alembic.
revision = "0001_initial"
down_revision = None
branch_labels = None
depends_on = None


def upgrade() -> None:
    bind = op.get_bind()
    models.Base.metadata.create_all(bind)


def downgrade() -> None:
    bind = op.get_bind()
    models.Base.metadata.drop_all(bind)
