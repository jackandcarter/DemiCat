import json
import aiomysql


class Database:
    """Simple wrapper around an aiomysql connection pool."""

    def __init__(self, config: dict):
        self._cfg = config
        self.pool: aiomysql.Pool | None = None

    async def connect(self) -> None:
        self.pool = await aiomysql.create_pool(
            host=self._cfg["mysql_host"],
            user=self._cfg["mysql_user"],
            password=self._cfg["mysql_password"],
            db=self._cfg["mysql_db"],
            autocommit=True,
        )

    async def close(self) -> None:
        if self.pool:
            self.pool.close()
            await self.pool.wait_closed()

    async def get_server_settings(self, guild_id: str) -> dict:
        if not self.pool:
            return {}
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute("SELECT settings FROM server_settings WHERE guild_id=%s", (guild_id,))
                row = await cur.fetchone()
                if row and row[0]:
                    return json.loads(row[0])
        return {}

    async def set_server_settings(self, guild_id: str, settings: dict) -> None:
        if not self.pool:
            return
        data = json.dumps(settings)
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    """
                    INSERT INTO server_settings (guild_id, settings)
                    VALUES (%s, %s)
                    ON DUPLICATE KEY UPDATE settings=VALUES(settings)
                    """,
                    (guild_id, data),
                )

    async def get_user_roles(self, key: str) -> list[str] | None:
        """Return a list of role IDs for the user associated with ``key``.

        The key uniquely identifies a user in the ``users`` table. If the key
        is invalid or no connection pool is available, ``None`` is returned so
        callers can treat the request as unauthorized.
        """
        if not self.pool:
            return None
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute("SELECT id, server_id FROM users WHERE `key`=%s", (key,))
                row = await cur.fetchone()
                if not row:
                    return None
                user_id, server_id = row
                await cur.execute(
                    "SELECT role_id FROM user_roles WHERE server_id=%s AND user_id=%s",
                    (server_id, user_id),
                )
                rows = await cur.fetchall()
                return [r[0] for r in rows]
