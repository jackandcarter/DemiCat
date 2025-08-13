import json
import aiomysql


class Database:
    """Simple wrapper around an aiomysql connection pool."""

    def __init__(self, config: dict):
        self._cfg = config
        self.pool: aiomysql.Pool | None = None

    async def connect(self) -> None:
        """Create the connection pool and ensure required tables exist."""

        self.pool = await aiomysql.create_pool(
            host=self._cfg["mysql_host"],
            user=self._cfg["mysql_user"],
            password=self._cfg["mysql_password"],
            db=self._cfg["mysql_db"],
            autocommit=True,
        )

        # Lazily create the schema so the application can operate against a
        # clean database without requiring a separate migration step.  This
        # mirrors the behaviour of the JavaScript implementation.
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    """
                    CREATE TABLE IF NOT EXISTS servers (
                        id VARCHAR(255) PRIMARY KEY
                    )
                    """
                )
                await cur.execute(
                    """
                    CREATE TABLE IF NOT EXISTS users (
                        id VARCHAR(255) PRIMARY KEY,
                        `key` VARCHAR(255),
                        character_name VARCHAR(255),
                        server_id VARCHAR(255),
                        FOREIGN KEY (server_id) REFERENCES servers(id),
                        INDEX (server_id)
                    )
                    """
                )
                await cur.execute(
                    """
                    CREATE TABLE IF NOT EXISTS channels (
                        id VARCHAR(255) PRIMARY KEY,
                        server_id VARCHAR(255),
                        type ENUM('event','fc_chat','officer_chat') NOT NULL,
                        FOREIGN KEY (server_id) REFERENCES servers(id)
                    )
                    """
                )
                await cur.execute(
                    """
                    CREATE TABLE IF NOT EXISTS events (
                        id INT AUTO_INCREMENT PRIMARY KEY,
                        user_id VARCHAR(255),
                        channel_id VARCHAR(255),
                        message_id VARCHAR(255),
                        title TEXT,
                        description TEXT,
                        time TEXT,
                        metadata TEXT,
                        FOREIGN KEY (user_id) REFERENCES users(id),
                        FOREIGN KEY (channel_id) REFERENCES channels(id)
                    )
                    """
                )
                await cur.execute(
                    """
                    CREATE TABLE IF NOT EXISTS event_attendance (
                        event_id INT,
                        user_id VARCHAR(255),
                        status ENUM('yes','maybe','no') NOT NULL,
                        PRIMARY KEY (event_id, user_id),
                        FOREIGN KEY (event_id) REFERENCES events(id) ON DELETE CASCADE,
                        FOREIGN KEY (user_id) REFERENCES users(id)
                    )
                    """
                )
                await cur.execute(
                    """
                    CREATE TABLE IF NOT EXISTS api_keys (
                        api_key VARCHAR(255) PRIMARY KEY,
                        user_id VARCHAR(255),
                        is_admin BOOLEAN DEFAULT FALSE,
                        FOREIGN KEY (user_id) REFERENCES users(id)
                    )
                    """
                )
                await cur.execute(
                    """
                    CREATE TABLE IF NOT EXISTS officer_roles (
                        server_id VARCHAR(255),
                        role_id VARCHAR(255),
                        PRIMARY KEY (server_id, role_id),
                        FOREIGN KEY (server_id) REFERENCES servers(id),
                        INDEX (server_id)
                    )
                    """
                )
                await cur.execute(
                    """
                    CREATE TABLE IF NOT EXISTS user_roles (
                        server_id VARCHAR(255),
                        user_id VARCHAR(255),
                        role_id VARCHAR(255),
                        PRIMARY KEY (server_id, user_id, role_id),
                        FOREIGN KEY (server_id) REFERENCES servers(id),
                        FOREIGN KEY (user_id) REFERENCES users(id),
                        INDEX (server_id),
                        INDEX (user_id)
                    )
                    """
                )
                # Settings are stored as a JSON blob per guild similar to the
                # original Python implementation.
                await cur.execute(
                    """
                    CREATE TABLE IF NOT EXISTS server_settings (
                        guild_id VARCHAR(255) PRIMARY KEY,
                        settings TEXT
                    )
                    """
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

    async def update_server_settings(self, guild_id: str, updates: dict) -> None:
        """Fetch, merge, and persist server settings for ``guild_id``."""
        settings = await self.get_server_settings(guild_id)
        settings.update(updates)
        await self.set_server_settings(guild_id, settings)

    # ------------------------------------------------------------------
    # User and key management
    # ------------------------------------------------------------------

    async def set_key(self, user_id: str, key: str, server_id: str) -> None:
        """Associate ``key`` with ``user_id`` on ``server_id``."""
        if not self.pool:
            return
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    "INSERT IGNORE INTO servers (id) VALUES (%s)",
                    (server_id,),
                )
                await cur.execute(
                    """
                    INSERT INTO users (id, `key`, server_id)
                    VALUES (%s, %s, %s)
                    ON DUPLICATE KEY UPDATE `key`=VALUES(`key`),
                                            server_id=VALUES(server_id)
                    """,
                    (user_id, key, server_id),
                )

    async def get_key(self, user_id: str) -> str | None:
        """Return the key associated with ``user_id`` if present."""
        if not self.pool:
            return None
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    "SELECT `key` FROM users WHERE id=%s",
                    (user_id,),
                )
                row = await cur.fetchone()
                return row[0] if row else None

    async def set_character(self, user_id: str, character_name: str) -> None:
        """Persist the user's character name."""
        if not self.pool:
            return
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    """
                    INSERT INTO users (id, character_name)
                    VALUES (%s, %s)
                    ON DUPLICATE KEY UPDATE character_name=VALUES(character_name)
                    """,
                    (user_id, character_name),
                )

    async def get_user_by_key(self, key: str) -> dict | None:
        """Return user information for the given API ``key``."""
        if not self.pool:
            return None
        async with self.pool.acquire() as conn:
            async with conn.cursor(aiomysql.DictCursor) as cur:
                await cur.execute(
                    """
                    SELECT id AS userId, character_name AS characterName,
                           server_id AS serverId
                    FROM users
                    WHERE `key`=%s
                    """,
                    (key,),
                )
                row = await cur.fetchone()
                if not row:
                    return None
                return {
                    "userId": str(row["userId"]),
                    "characterName": row["characterName"],
                    "serverId": str(row["serverId"]),
                }

    async def set_user_roles(self, server_id: str, user_id: str, roles: list[str]) -> None:
        """Persist the list of role tags for ``user_id`` on ``server_id``."""
        if not self.pool:
            return
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    "INSERT IGNORE INTO servers (id) VALUES (%s)",
                    (server_id,),
                )
                await cur.execute(
                    """
                    INSERT INTO users (id, server_id)
                    VALUES (%s, %s)
                    ON DUPLICATE KEY UPDATE server_id=VALUES(server_id)
                    """,
                    (user_id, server_id),
                )
                await cur.execute(
                    "DELETE FROM user_roles WHERE server_id=%s AND user_id=%s",
                    (server_id, user_id),
                )
                for role_id in roles:
                    await cur.execute(
                        "INSERT INTO user_roles (server_id, user_id, role_id) VALUES (%s, %s, %s)",
                        (server_id, user_id, role_id),
                    )

    async def map_role_ids_to_tags(self, server_id: str, roles: list[str]) -> list[str]:
        """Map Discord role IDs in ``roles`` to logical role tags.

        The current implementation recognises two tags:

        ``officer``
            Assigned when any of the provided IDs match those recorded via
            :meth:`set_officer_roles` for ``server_id``.

        ``chat``
            Assigned when the user has *any* role in ``roles``.  This mirrors
            the behaviour of the original DemiCat implementation where access
            to Free Company chat is granted to all members with at least one
            role.
        """

        tags: list[str] = []

        officer_roles = set(await self.get_officer_roles(server_id))
        if any(r in officer_roles for r in roles):
            tags.append("officer")

        if roles:
            tags.append("chat")

        return tags

    async def get_user_roles_for_user(self, server_id: str, user_id: str) -> list[str]:
        """Return role tags for ``user_id`` on ``server_id``."""
        if not self.pool:
            return []
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    "SELECT role_id FROM user_roles WHERE server_id=%s AND user_id=%s",
                    (server_id, user_id),
                )
                rows = await cur.fetchall()
                return [r[0] for r in rows]

    # ------------------------------------------------------------------
    # Channel helpers
    # ------------------------------------------------------------------

    async def get_event_channels(self) -> list[str]:
        """Return all channel IDs configured for events across servers."""
        if not self.pool:
            return []
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute("SELECT settings FROM server_settings")
                rows = await cur.fetchall()
                channels: list[str] = []
                for row in rows:
                    try:
                        data = json.loads(row[0])
                        arr = data.get("eventChannels")
                        if isinstance(arr, list):
                            channels.extend(str(c) for c in arr)
                    except Exception:
                        continue
                return channels

    async def get_fc_channels(self) -> list[str]:
        """Return all FC chat channel IDs."""
        if not self.pool:
            return []
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute("SELECT settings FROM server_settings")
                rows = await cur.fetchall()
                channels: list[str] = []
                for row in rows:
                    try:
                        data = json.loads(row[0])
                        ch = data.get("fcChatChannel")
                        if ch:
                            channels.append(str(ch))
                    except Exception:
                        continue
                return channels

    async def get_officer_channels(self) -> list[str]:
        """Return all officer chat channel IDs."""
        if not self.pool:
            return []
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute("SELECT settings FROM server_settings")
                rows = await cur.fetchall()
                channels: list[str] = []
                for row in rows:
                    try:
                        data = json.loads(row[0])
                        ch = data.get("officerChatChannel")
                        if ch:
                            channels.append(str(ch))
                    except Exception:
                        continue
                return channels

    async def get_user_roles(self, key: str) -> list[str] | None:
        """Return a list of role tags for the user associated with ``key``.

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

    async def set_officer_roles(self, server_id: str, roles: list[str]) -> None:
        """Replace officer roles for ``server_id`` with ``roles``."""
        if not self.pool:
            return
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    "INSERT IGNORE INTO servers (id) VALUES (%s)",
                    (server_id,),
                )
                await cur.execute(
                    "DELETE FROM officer_roles WHERE server_id=%s",
                    (server_id,),
                )
                for role_id in roles:
                    await cur.execute(
                        "INSERT INTO officer_roles (server_id, role_id) VALUES (%s, %s)",
                        (server_id, role_id),
                    )

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

    async def get_events(self, channel_id: str) -> list[dict]:
        """Return all events for ``channel_id``."""
        if not self.pool:
            return []
        async with self.pool.acquire() as conn:
            async with conn.cursor(aiomysql.DictCursor) as cur:
                await cur.execute(
                    "SELECT * FROM events WHERE channel_id=%s",
                    (channel_id,),
                )
                return await cur.fetchall()

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

    async def get_event_by_message_id(self, message_id: str) -> dict | None:
        """Return event information by message ID."""
        if not self.pool:
            return None
        async with self.pool.acquire() as conn:
            async with conn.cursor(aiomysql.DictCursor) as cur:
                await cur.execute(
                    "SELECT * FROM events WHERE message_id=%s",
                    (message_id,),
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

    async def set_event_attendance(self, event_id: int, user_id: str, status: str) -> None:
        if not self.pool:
            return
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    """
                    INSERT INTO event_attendance (event_id, user_id, status)
                    VALUES (%s, %s, %s)
                    ON DUPLICATE KEY UPDATE status=VALUES(status)
                    """,
                    (event_id, user_id, status),
                )

    async def clear_server(self, server_id: str) -> None:
        """Remove all data associated with ``server_id``."""
        if not self.pool:
            return
        async with self.pool.acquire() as conn:
            async with conn.cursor() as cur:
                await cur.execute(
                    "DELETE e FROM events e JOIN channels c ON e.channel_id=c.id WHERE c.server_id=%s",
                    (server_id,),
                )
                await cur.execute(
                    "DELETE ak FROM api_keys ak JOIN users u ON ak.user_id=u.id WHERE u.server_id=%s",
                    (server_id,),
                )
                await cur.execute(
                    "DELETE FROM server_settings WHERE guild_id=%s",
                    (server_id,),
                )
                await cur.execute(
                    "DELETE FROM officer_roles WHERE server_id=%s",
                    (server_id,),
                )
                await cur.execute(
                    "DELETE FROM user_roles WHERE server_id=%s",
                    (server_id,),
                )
                await cur.execute(
                    "DELETE FROM channels WHERE server_id=%s",
                    (server_id,),
                )
                await cur.execute(
                    "DELETE FROM users WHERE server_id=%s",
                    (server_id,),
                )
                await cur.execute(
                    "DELETE FROM servers WHERE id=%s",
                    (server_id,),
                )
