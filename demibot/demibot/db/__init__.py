"""Database package exports and side effect imports.

This module exposes the declarative ``Base`` for other modules to use and
imports the models package for its side effects.  Importing ``models`` ensures
that all ORM model classes are registered on ``Base.metadata`` so that tools
like Alembic can discover them during autogeneration and migrations.
"""

from .base import Base

# Import models so Alembic autogeneration can pick them up.  The imported names
# are not re-exported but the import has the desired side effect of registering
# all models with ``Base.metadata``.
from . import models  # noqa: F401
from models import event as _event  # noqa: F401

__all__ = ["Base"]
