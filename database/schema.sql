CREATE TABLE IF NOT EXISTS servers (
  id VARCHAR(255) PRIMARY KEY
);

CREATE TABLE IF NOT EXISTS users (
  id VARCHAR(255) PRIMARY KEY,
  `key` VARCHAR(255),
  character_name VARCHAR(255),
  server_id VARCHAR(255),
  FOREIGN KEY (server_id) REFERENCES servers(id),
  INDEX (server_id)
);

CREATE TABLE IF NOT EXISTS channels (
  id VARCHAR(255) PRIMARY KEY,
  server_id VARCHAR(255),
  type ENUM('event','fc_chat','officer_chat') NOT NULL,
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

CREATE TABLE IF NOT EXISTS event_attendance (
  event_id INT,
  user_id VARCHAR(255),
  status ENUM('yes','maybe','no') NOT NULL,
  PRIMARY KEY (event_id, user_id),
  FOREIGN KEY (event_id) REFERENCES events(id) ON DELETE CASCADE,
  FOREIGN KEY (user_id) REFERENCES users(id)
);

CREATE TABLE IF NOT EXISTS api_keys (
  api_key VARCHAR(255) PRIMARY KEY,
  user_id VARCHAR(255),
  is_admin BOOLEAN DEFAULT FALSE,
  FOREIGN KEY (user_id) REFERENCES users(id)
);

CREATE TABLE IF NOT EXISTS officer_roles (
  server_id VARCHAR(255),
  role_id VARCHAR(255),
  PRIMARY KEY (server_id, role_id),
  FOREIGN KEY (server_id) REFERENCES servers(id),
  INDEX (server_id)
);

CREATE TABLE IF NOT EXISTS user_roles (
  server_id VARCHAR(255),
  user_id VARCHAR(255),
  role_id VARCHAR(255),
  PRIMARY KEY (server_id, user_id, role_id),
  FOREIGN KEY (server_id) REFERENCES servers(id),
  FOREIGN KEY (user_id) REFERENCES users(id),
  INDEX (server_id),
  INDEX (user_id)
);

CREATE TABLE IF NOT EXISTS server_settings (
  guild_id VARCHAR(255) PRIMARY KEY,
  settings TEXT
);

