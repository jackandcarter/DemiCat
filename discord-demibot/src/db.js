const mysql = require('mysql2/promise');

let pool;

async function init(config) {
  pool = mysql.createPool({
    host: config.host,
    user: config.user,
    password: config.password,
    database: config.name,
    waitForConnections: true,
    connectionLimit: 10,
    queueLimit: 0
  });

  await pool.query(`CREATE TABLE IF NOT EXISTS servers (
    id VARCHAR(255) PRIMARY KEY
  )`);

  await pool.query(`CREATE TABLE IF NOT EXISTS users (
    id VARCHAR(255) PRIMARY KEY,
    \`key\` VARCHAR(255),
    character VARCHAR(255),
    server_id VARCHAR(255),
    FOREIGN KEY (server_id) REFERENCES servers(id),
    INDEX (server_id)
  )`);

  await pool.query(`CREATE TABLE IF NOT EXISTS channels (
    id VARCHAR(255) PRIMARY KEY,
    server_id VARCHAR(255),
    type ENUM('event','fc_chat','officer_chat') NOT NULL,
    FOREIGN KEY (server_id) REFERENCES servers(id)
  )`);

  await pool.query(`CREATE TABLE IF NOT EXISTS events (
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
  )`);

  await pool.query(`CREATE TABLE IF NOT EXISTS api_keys (
    api_key VARCHAR(255) PRIMARY KEY,
    user_id VARCHAR(255),
    is_admin BOOLEAN DEFAULT FALSE,
    FOREIGN KEY (user_id) REFERENCES users(id)
  )`);

  await pool.query(`CREATE TABLE IF NOT EXISTS officer_roles (
    server_id VARCHAR(255),
    role_id VARCHAR(255),
    PRIMARY KEY (server_id, role_id),
    FOREIGN KEY (server_id) REFERENCES servers(id),
    INDEX (server_id)
  )`);

  await pool.query(`CREATE TABLE IF NOT EXISTS user_roles (
    server_id VARCHAR(255),
    user_id VARCHAR(255),
    role_id VARCHAR(255),
    PRIMARY KEY (server_id, user_id, role_id),
    FOREIGN KEY (server_id) REFERENCES servers(id),
    FOREIGN KEY (user_id) REFERENCES users(id),
    INDEX (server_id),
    INDEX (user_id)
  )`);

  await pool.query(`CREATE TABLE IF NOT EXISTS server_settings (
    server_id VARCHAR(255),
    setting_key VARCHAR(255),
    setting_value TEXT,
    PRIMARY KEY (server_id, setting_key),
    FOREIGN KEY (server_id) REFERENCES servers(id),
    INDEX (server_id)
  )`);
}

async function query(sql, params) {
  const [rows] = await pool.execute(sql, params);
  return rows;
}

async function one(sql, params) {
  const rows = await query(sql, params);
  return rows[0] || null;
}

async function tx(cb) {
  const conn = await pool.getConnection();
  try {
    await conn.beginTransaction();
    const res = await cb({
      query: (sql, params) => conn.execute(sql, params).then(([rows]) => rows),
      one: (sql, params) => conn.execute(sql, params).then(([rows]) => rows[0] || null),
    });
    await conn.commit();
    return res;
  } catch (err) {
    await conn.rollback();
    throw err;
  } finally {
    conn.release();
  }
}

async function setKey(userId, key, serverId) {
  await query('INSERT IGNORE INTO servers (id) VALUES (?)', [serverId]);
  await query(
    'INSERT INTO users (id, \`key\`, server_id) VALUES (?, ?, ?) ON DUPLICATE KEY UPDATE \`key\` = VALUES(\`key\`), server_id = VALUES(server_id)',
    [userId, key, serverId]
  );
}

async function getKey(userId) {
  const row = await one('SELECT `key` FROM users WHERE id = ?', [userId]);
  return row ? row.key : null;
}

async function setCharacter(userId, character) {
  await query(
    'INSERT INTO users (id, character) VALUES (?, ?) ON DUPLICATE KEY UPDATE character = VALUES(character)',
    [userId, character]
  );
}

async function getUserByKey(key) {
  return await one('SELECT id AS userId, character, server_id AS serverId FROM users WHERE \`key\` = ?', [key]);
}

async function getEventChannels() {
  const rows = await query(
    "SELECT setting_value FROM server_settings WHERE setting_key = 'eventChannels'",
    []
  );
  const channels = [];
  for (const row of rows) {
    try {
      const arr = JSON.parse(row.setting_value);
      if (Array.isArray(arr)) channels.push(...arr);
    } catch {}
  }
  return channels;
}

async function getFcChannels() {
  const rows = await query(
    "SELECT setting_value FROM server_settings WHERE setting_key = 'fcChatChannel'",
    []
  );
  return rows.map(r => {
    try {
      return JSON.parse(r.setting_value);
    } catch {
      return r.setting_value;
    }
  });
}

async function getOfficerChannels() {
  const rows = await query(
    "SELECT setting_value FROM server_settings WHERE setting_key = 'officerChatChannel'",
    []
  );
  return rows.map(r => {
    try {
      return JSON.parse(r.setting_value);
    } catch {
      return r.setting_value;
    }
  });
}

async function setOfficerRoles(serverId, roles) {
  await tx(async ({ query }) => {
    await query('INSERT IGNORE INTO servers (id) VALUES (?)', [serverId]);
    await query('DELETE FROM officer_roles WHERE server_id = ?', [serverId]);
    for (const roleId of roles) {
      await query('INSERT INTO officer_roles (server_id, role_id) VALUES (?, ?)', [serverId, roleId]);
    }
  });
}

async function saveEvent(event) {
  const res = await query(
    'INSERT INTO events (user_id, channel_id, message_id, title, description, time, metadata) VALUES (?, ?, ?, ?, ?, ?, ?)',
    [
      event.userId,
      event.channelId,
      event.messageId,
      event.title,
      event.description,
      event.time,
      event.metadata
    ]
  );
  return res.insertId;
}

async function getEvents(channelId) {
  return await query('SELECT * FROM events WHERE channel_id = ?', [channelId]);
}

async function getEvent(id) {
  return await one('SELECT * FROM events WHERE id = ?', [id]);
}

async function updateEvent(event) {
  await query(
    'UPDATE events SET title = ?, description = ?, time = ?, metadata = ? WHERE id = ?',
    [event.title, event.description, event.time, event.metadata, event.id]
  );
}

async function getApiKey(key) {
  return await one(
    'SELECT ak.user_id AS userId, ak.is_admin AS isAdmin, u.character, u.server_id AS serverId FROM api_keys ak LEFT JOIN users u ON ak.user_id = u.id WHERE ak.api_key = ?',
    [key]
  );
}

async function getOfficerRoles(serverId) {
  const rows = await query('SELECT role_id FROM officer_roles WHERE server_id = ?', [serverId]);
  return rows.map(r => r.role_id);
}

async function setServerSettings(serverId, settings) {
  await tx(async ({ query }) => {
    await query('INSERT IGNORE INTO servers (id) VALUES (?)', [serverId]);
    for (const [key, value] of Object.entries(settings)) {
      await query(
        'REPLACE INTO server_settings (server_id, setting_key, setting_value) VALUES (?, ?, ?)',
        [serverId, key, JSON.stringify(value)]
      );
    }
  });
}

async function getServerSettings(serverId) {
  const rows = await query('SELECT setting_key, setting_value FROM server_settings WHERE server_id = ?', [serverId]);
  const settings = {};
  for (const row of rows) {
    try {
      settings[row.setting_key] = JSON.parse(row.setting_value);
    } catch {
      settings[row.setting_key] = row.setting_value;
    }
  }
  return settings;
}

async function setUserRoles(serverId, userId, roles) {
  await tx(async ({ query }) => {
    await query('INSERT IGNORE INTO servers (id) VALUES (?)', [serverId]);
    await query(
      'INSERT INTO users (id, server_id) VALUES (?, ?) ON DUPLICATE KEY UPDATE server_id = VALUES(server_id)',
      [userId, serverId]
    );
    await query('DELETE FROM user_roles WHERE server_id = ? AND user_id = ?', [serverId, userId]);
    for (const roleId of roles) {
      await query('INSERT INTO user_roles (server_id, user_id, role_id) VALUES (?, ?, ?)', [serverId, userId, roleId]);
    }
  });
}

async function getUserRoles(serverId, userId) {
  const rows = await query('SELECT role_id FROM user_roles WHERE server_id = ? AND user_id = ?', [serverId, userId]);
  return rows.map(r => r.role_id);
}

// Remove all data associated with a server/guild
async function clearServer(serverId) {
  await tx(async ({ query }) => {
    // remove events linked via channels first
    await query(
      'DELETE e FROM events e JOIN channels c ON e.channel_id = c.id WHERE c.server_id = ?',
      [serverId]
    );
    // remove API keys for users in this server
    await query(
      'DELETE ak FROM api_keys ak JOIN users u ON ak.user_id = u.id WHERE u.server_id = ?',
      [serverId]
    );
    await query('DELETE FROM server_settings WHERE server_id = ?', [serverId]);
    await query('DELETE FROM officer_roles WHERE server_id = ?', [serverId]);
    await query('DELETE FROM user_roles WHERE server_id = ?', [serverId]);
    await query('DELETE FROM channels WHERE server_id = ?', [serverId]);
    await query('DELETE FROM users WHERE server_id = ?', [serverId]);
    await query('DELETE FROM servers WHERE id = ?', [serverId]);
  });
}

module.exports = {
  init,
  query,
  one,
  tx,
  get pool() {
    return pool;
  },
  setKey,
  getKey,
  setCharacter,
  getUserByKey,
  getEventChannels,
  getFcChannels,
  getOfficerChannels,
  saveEvent,
  getEvents,
  getEvent,
  updateEvent,
  getApiKey,
  getOfficerRoles,
  setOfficerRoles,
  setServerSettings,
  getServerSettings,
  setUserRoles,
  getUserRoles,
  clearServer
};

