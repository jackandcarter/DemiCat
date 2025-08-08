CREATE TABLE IF NOT EXISTS users (
  id VARCHAR(255) PRIMARY KEY,
  `key` VARCHAR(255),
  character VARCHAR(255)
);

CREATE TABLE IF NOT EXISTS servers (
  id VARCHAR(255) PRIMARY KEY
);

CREATE TABLE IF NOT EXISTS channels (
  id VARCHAR(255) PRIMARY KEY,
  server_id VARCHAR(255),
  type VARCHAR(32) NOT NULL,
  FOREIGN KEY (server_id) REFERENCES servers(id)
);

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
);

CREATE TABLE IF NOT EXISTS api_keys (
  api_key VARCHAR(255) PRIMARY KEY,
  user_id VARCHAR(255),
  is_admin BOOLEAN DEFAULT FALSE,
  FOREIGN KEY (user_id) REFERENCES users(id)
);
