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

  await pool.query(`CREATE TABLE IF NOT EXISTS users (
    id VARCHAR(255) PRIMARY KEY,
    \`key\` VARCHAR(255),
    character VARCHAR(255)
  )`);

  await pool.query(`CREATE TABLE IF NOT EXISTS servers (
    id VARCHAR(255) PRIMARY KEY
  )`);

  await pool.query(`CREATE TABLE IF NOT EXISTS channels (
    id VARCHAR(255) PRIMARY KEY,
    server_id VARCHAR(255),
    type VARCHAR(32) NOT NULL,
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

async function setKey(userId, key) {
  await query(
    'INSERT INTO users (id, \`key\`) VALUES (?, ?) ON DUPLICATE KEY UPDATE \`key\` = VALUES(\`key\`)',
    [userId, key]
  );
}

async function setCharacter(userId, character) {
  await query(
    'INSERT INTO users (id, character) VALUES (?, ?) ON DUPLICATE KEY UPDATE character = VALUES(character)',
    [userId, character]
  );
}

async function getUserByKey(key) {
  return await one('SELECT id AS userId, character FROM users WHERE \`key\` = ?', [key]);
}

async function getEventChannels() {
  const rows = await query('SELECT id FROM channels WHERE type = ?', ['event']);
  return rows.map(r => r.id);
}

async function addEventChannel(channelId) {
  await query('INSERT IGNORE INTO channels (id, type) VALUES (?, ?)', [channelId, 'event']);
}

async function getChatChannels() {
  const rows = await query('SELECT id FROM channels WHERE type = ?', ['chat']);
  return rows.map(r => r.id);
}

async function addChatChannel(channelId) {
  await query('INSERT IGNORE INTO channels (id, type) VALUES (?, ?)', [channelId, 'chat']);
}

async function saveEvent(event) {
  await query(
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
}

async function getEvents(channelId) {
  return await query('SELECT * FROM events WHERE channel_id = ?', [channelId]);
}

async function getApiKey(key) {
  return await one(
    'SELECT ak.user_id AS userId, ak.is_admin AS isAdmin, u.character FROM api_keys ak LEFT JOIN users u ON ak.user_id = u.id WHERE ak.api_key = ?',
    [key]
  );
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
  setCharacter,
  getUserByKey,
  getEventChannels,
  addEventChannel,
  getChatChannels,
  addChatChannel,
  saveEvent,
  getEvents,
  getApiKey
};

