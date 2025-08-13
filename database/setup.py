#!/usr/bin/env python3
from getpass import getpass
from pathlib import Path
import argparse
import logging

from python_demibot.logging_config import setup_logging

logger = logging.getLogger(__name__)

try:
    import mysql.connector as mysql
    CONNECTOR = 'mysql.connector'
except ImportError:
    try:
        import pymysql as mysql
        CONNECTOR = 'pymysql'
    except ImportError:
        raise SystemExit('Install mysql-connector-python or PyMySQL')


def get_connection(host: str, port: int, user: str, password: str):
    if CONNECTOR == 'mysql.connector':
        return mysql.connect(host=host, port=port, user=user, password=password)
    return mysql.connect(host=host, port=port, user=user, password=password)


def execute_schema(cursor, schema_path: Path):
    sql = schema_path.read_text()
    statements = [s.strip() for s in sql.split(';') if s.strip()]
    for stmt in statements:
        cursor.execute(stmt)


def apply_migrations(cursor):
    cursor.execute("SHOW COLUMNS FROM users LIKE 'server_id'")
    if not cursor.fetchone():
        cursor.execute('ALTER TABLE users ADD COLUMN server_id VARCHAR(255)')
        cursor.execute('ALTER TABLE users ADD INDEX (server_id)')
        cursor.execute('ALTER TABLE users ADD CONSTRAINT fk_users_server FOREIGN KEY (server_id) REFERENCES servers(id)')

    cursor.execute("SHOW TABLES LIKE 'server_settings'")
    if cursor.fetchone():
        cursor.execute("SHOW COLUMNS FROM server_settings LIKE 'setting_key'")
        if cursor.fetchone():
            cursor.execute('DROP TABLE server_settings')
            cursor.execute(
                """
                CREATE TABLE server_settings (
                    guild_id VARCHAR(255) PRIMARY KEY,
                    settings TEXT
                )
                """
            )


def main():
    parser = argparse.ArgumentParser(description='Initialize the DemiBot database')
    parser.add_argument('--local', action='store_true', help='Use localhost with default port 3306')
    parser.add_argument('--host', help='MySQL host')
    parser.add_argument('--port', type=int, help='MySQL port')
    parser.add_argument('--user', help='MySQL user')
    parser.add_argument('--password', help='MySQL password')
    parser.add_argument('--schema', default=str(Path(__file__).with_name('schema.sql')), help='Path to schema.sql')
    parser.add_argument('--debug', action='store_true', help='Enable debug logging')
    args = parser.parse_args()

    setup_logging(debug=args.debug)

    host = 'localhost' if args.local else args.host
    if not host:
        use_local = input('Use localhost? [Y/n]: ').strip().lower()
        if use_local in ('', 'y', 'yes'):
            host = 'localhost'
        else:
            host = input('MySQL host: ').strip()

    port = args.port
    if not port:
        port_input = input('MySQL port [3306]: ').strip()
        port = int(port_input) if port_input else 3306

    user = args.user or input('MySQL user: ').strip()
    password = args.password or getpass('MySQL password: ')

    conn = get_connection(host, port, user, password)
    cursor = conn.cursor()

    cursor.execute('CREATE DATABASE IF NOT EXISTS DemiBot')
    conn.commit()
    cursor.execute('USE DemiBot')
    conn.commit()

    execute_schema(cursor, Path(args.schema))
    conn.commit()

    apply_migrations(cursor)
    conn.commit()

    cursor.close()
    conn.close()
    logger.info('Database DemiBot initialized.')


if __name__ == '__main__':
    main()
