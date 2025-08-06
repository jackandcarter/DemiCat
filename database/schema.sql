CREATE TABLE IF NOT EXISTS users (
  id TEXT PRIMARY KEY,
  key TEXT,
  character TEXT
);

CREATE TABLE IF NOT EXISTS servers (
  id TEXT PRIMARY KEY
);

CREATE TABLE IF NOT EXISTS channels (
  id TEXT PRIMARY KEY,
  server_id TEXT,
  type TEXT NOT NULL,
  FOREIGN KEY(server_id) REFERENCES servers(id)
);

CREATE TABLE IF NOT EXISTS events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id TEXT,
  channel_id TEXT,
  message_id TEXT,
  title TEXT,
  description TEXT,
  time TEXT,
  metadata TEXT,
  FOREIGN KEY(user_id) REFERENCES users(id),
  FOREIGN KEY(channel_id) REFERENCES channels(id)
);
