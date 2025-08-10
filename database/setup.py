#!/usr/bin/env python3
import argparse
from getpass import getpass
from pathlib import Path

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


def main():
    parser = argparse.ArgumentParser(description='Initialize the DemiBot database')
    parser.add_argument('--local', action='store_true', help='Use localhost with default port 3306')
    parser.add_argument('--host', help='MySQL host')
    parser.add_argument('--port', type=int, help='MySQL port')
    parser.add_argument('--user', help='MySQL user')
    parser.add_argument('--password', help='MySQL password')
    parser.add_argument('--schema', default=str(Path(__file__).with_name('schema.sql')), help='Path to schema.sql')
    args = parser.parse_args()

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

    cursor.close()
    conn.close()
    print('Database DemiBot initialized.')


if __name__ == '__main__':
    main()
