from __future__ import annotations

import os
from logging.config import fileConfig
from sqlalchemy import engine_from_config, pool
from alembic import context

# Import metadata so autogenerate works
from demibot.db.base import Base  # noqa: F401
from demibot.db import models  # noqa: F401

# Alembic version table expanded to 128 characters
# to allow descriptive revision identifiers.


# this is the Alembic Config object, which provides
# access to the values within the .ini file in use.
config = context.config

# Interpret the config file for Python logging.
# This line sets up loggers basically.
if config.config_file_name is not None and config.get_section("loggers"):
    fileConfig(config.config_file_name)


def _normalize_sqlalchemy_url(url: str) -> str:
    """
    Ensure migrations use a synchronous driver and TCP.
    - Convert mysql+aiomysql -> mysql+pymysql
    - Replace @localhost -> @127.0.0.1 (avoid socket auth differences)
    """
    if not url:
        return url
    if url.startswith("mysql+aiomysql://"):
        url = "mysql+pymysql://" + url.split("mysql+aiomysql://", 1)[1]
    # Only replace a host token after '@' to avoid touching passwords
    url = url.replace("@localhost", "@127.0.0.1")
    return url


# Prefer DATABASE_URL/DEMIBOT_DATABASE_URL over alembic.ini
env_url = (os.getenv("DEMIBOT_DATABASE_URL")
           or os.getenv("DATABASE_URL")
           or config.get_main_option("sqlalchemy.url"))

# Fallback sensible default for local dev
if not env_url:
    env_url = "mysql+pymysql://demibot:Admin@127.0.0.1:3306/demibot"

norm_url = _normalize_sqlalchemy_url(env_url)
config.set_main_option("sqlalchemy.url", norm_url)

# Optional: print once so it's obvious which DSN Alembic is using
print("ALEMBIC sqlalchemy.url =", norm_url)


target_metadata = Base.metadata


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
    """Run migrations in 'online' mode."""
    connectable = engine_from_config(
        config.get_section(config.config_ini_section),
        prefix="sqlalchemy.",
        poolclass=pool.NullPool,
        # pre_ping helps recycle stale connections
        pool_pre_ping=True,
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
