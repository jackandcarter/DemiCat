# Migration Guidelines

The Python service uses Alembic for database migrations. Revision identifiers
are stored in the `alembic_version` table. To accommodate descriptive names,
`alembic_version.version_num` is configured for up to 128 characters (see
migration `0018_expand_version_num_to_128_chars` and
`demibot/demibot/db/migrations/env.py`).

When creating a new migration:

- Start the filename and `revision` string with the next sequential number
  (e.g. `0019_descriptive_change`).
- Keep the full revision identifier under 128 characters so it fits in the
  version table.

Following these guidelines prevents future migrations from exceeding the
`version_num` column's length limit.
