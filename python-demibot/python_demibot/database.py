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

    async def get_api_key(self, key: str) -> dict | None:
        """Validate ``key`` against the ``api_keys`` table.

        Returns a dictionary containing ``userId``, ``isAdmin``,
        ``characterName`` and ``serverId`` if the key exists, otherwise
        ``None``.
        """
        if not self.pool:
            return None
        async with self.pool.acquire() as conn:
            async with conn.cursor(aiomysql.DictCursor) as cur:
                await cur.execute(
                    """
                    SELECT ak.user_id AS userId, ak.is_admin AS isAdmin,
                           u.character_name AS characterName, u.server_id AS serverId
                    FROM api_keys ak LEFT JOIN users u ON ak.user_id = u.id
                    WHERE ak.api_key=%s
                    """,
                    (key,),
                )
                row = await cur.fetchone()
                if not row:
                    return None
                return {
                    "userId": str(row["userId"]),
                    "isAdmin": bool(row["isAdmin"]),
                    "characterName": row["characterName"],
                    "serverId": str(row["serverId"]),
                }

    async def get_officer_roles(self, server_id: str) -> list[str]:
        """Return a list of role IDs marked as officer roles for ``server_id``."""
        if not self.pool:
            return []
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    "SELECT role_id FROM officer_roles WHERE server_id=%s",
                    (server_id,),
                )
                rows = await cur.fetchall()
                return [r[0] for r in rows]

    async def save_event(self, event: dict) -> int:
        """Persist a new event and return its row ID."""
        if not self.pool:
            return 0
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    """
                    INSERT INTO events (user_id, channel_id, message_id, title, description, time, metadata)
                    VALUES (%s, %s, %s, %s, %s, %s, %s)
                    """,
                    (
                        event["userId"],
                        event["channelId"],
                        event["messageId"],
                        event["title"],
                        event["description"],
                        event["time"],
                        event.get("metadata"),
                    ),
                )
                return cur.lastrowid or 0

    async def get_event(self, event_id: int) -> dict | None:
        """Return event information by ID."""
        if not self.pool:
            return None
        async with self.pool.acquire() as conn:
            async with conn.cursor(aiomysql.DictCursor) as cur:
                await cur.execute(
                    "SELECT * FROM events WHERE id=%s",
                    (event_id,),
                )
                return await cur.fetchone()

    async def update_event(self, event: dict) -> None:
        """Update an existing event."""
        if not self.pool:
            return
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    """
                    UPDATE events SET title=%s, description=%s, time=%s, metadata=%s
                    WHERE id=%s
                    """,
                    (
                        event["title"],
                        event["description"],
                        event["time"],
                        event.get("metadata"),
                        event["id"],
                    ),
                )
