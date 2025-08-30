from __future__ import annotations

import os
from logging.config import fileConfig

from alembic import context
from sqlalchemy import engine_from_config, pool
from sqlalchemy.engine import make_url

# Import metadata so autogenerate works
from demibot.db.base import Base  # noqa: F401
from demibot.db import models  # noqa: F401  # keeps model metadata registered


# ---------------------------
# Alembic base configuration
# ---------------------------
config = context.config

# Configure logging if present
if config.config_file_name is not None and config.get_section("loggers"):
    fileConfig(config.config_file_name)


# ---------------------------
# Helpers
# ---------------------------
def _normalize_sqlalchemy_url(url: str) -> str:
    """
    Normalize the SQLAlchemy URL for Alembic:
      - Force synchronous driver (mysql+pymysql)
      - Force TCP host (127.0.0.1) instead of localhost
      - Strip accidental whitespace/newlines
    """
    if not url:
        return url
    url = url.strip()

    # Convert async driver to sync driver for Alembic
    if url.startswith("mysql+aiomysql://"):
        url = "mysql+pymysql://" + url[len("mysql+aiomysql://"):]
    if url.startswith("mysql+asyncmy://"):
        url = "mysql+pymysql://" + url[len("mysql+asyncmy://"):]
    # Ensure driver prefix is present (optional safety)
    if url.startswith("mysql://"):
        url = "mysql+pymysql://" + url[len("mysql://"):]

    # Replace host token after '@' only (avoid touching password)
    # We’ll do a minimal, safe replace of '@localhost' with '@127.0.0.1'
    url = url.replace("@localhost", "@127.0.0.1")

    return url


def _debug_print_url_sources(final_url: str) -> None:
    """Print loud diagnostics about where the URL came from and what it is."""
    env_demibot = os.getenv("DEMIBOT_DATABASE_URL")
    env_generic = os.getenv("DATABASE_URL")
    env_forced = os.getenv("DEMIBOT_FORCED_URL")

    print("=== ALEMBIC DEBUG: URL SOURCES ===")
    print("DEMIBOT_FORCED_URL:", repr(env_forced))
    print("DEMIBOT_DATABASE_URL:", repr(env_demibot))
    print("DATABASE_URL:", repr(env_generic))
    print("alembic.ini sqlalchemy.url (raw):", repr(config.get_main_option("sqlalchemy.url")))
    print("=> Using (normalized) URL:", make_url(final_url).render_as_string(hide_password=False))
    print("URL components:",
          {
              "username": make_url(final_url).username,
              "password": make_url(final_url).password,
              "host": make_url(final_url).host,
              "port": make_url(final_url).port,
              "database": make_url(final_url).database,
              "drivername": make_url(final_url).drivername,
          })
    print("=== END ALEMBIC DEBUG ===")


# ---------------------------
# URL selection (with override)
# ---------------------------
# Order of precedence (highest first):
#   1) DEMIBOT_FORCED_URL (explicit override for debugging / CI)
#   2) DEMIBOT_DATABASE_URL
#   3) DATABASE_URL
#   4) alembic.ini sqlalchemy.url
#   5) sensible local default
forced_url = os.getenv("DEMIBOT_FORCED_URL")
if forced_url:
    chosen = forced_url
else:
    chosen = (
        os.getenv("DEMIBOT_DATABASE_URL")
        or os.getenv("DATABASE_URL")
        or config.get_main_option("sqlalchemy.url")
        or "mysql+pymysql://demibot:Admin@127.0.0.1:3306/demibot"
    )

norm_url = _normalize_sqlalchemy_url(chosen)
config.set_main_option("sqlalchemy.url", norm_url)

# Very explicit print so we can SEE the real value Alembic is using (password visible on purpose)
_debug_print_url_sources(norm_url)

target_metadata = Base.metadata


# ---------------------------
# Migration runners
# ---------------------------
def run_migrations_offline() -> None:
    """Run migrations in 'offline' mode."""
    url = config.get_main_option("sqlalchemy.url")
    context.configure(
        url=url,
        target_metadata=target_metadata,
        literal_binds=True,
        compare_type=True,
        compare_server_default=True,
    )

    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    """Run migrations in 'online' mode'."""
    connectable = engine_from_config(
        config.get_section(config.config_ini_section),
        prefix="sqlalchemy.",
        poolclass=pool.NullPool,
        pool_pre_ping=True,  # recycle stale connections
    )

    with connectable.connect() as connection:
        context.configure(
            connection=connection,
            target_metadata=target_metadata,
            compare_type=True,
            compare_server_default=True,
        )

        with context.begin_transaction():
            context.run_migrations()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
