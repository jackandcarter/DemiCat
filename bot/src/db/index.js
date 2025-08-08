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
}

async function setKey(userId, key) {
  await pool.execute(
    'INSERT INTO users (id, \`key\`) VALUES (?, ?) ON DUPLICATE KEY UPDATE \`key\` = VALUES(\`key\`)',
    [userId, key]
  );
}

async function setCharacter(userId, character) {
  await pool.execute(
    'INSERT INTO users (id, character) VALUES (?, ?) ON DUPLICATE KEY UPDATE character = VALUES(character)',
    [userId, character]
  );
}

async function getUserByKey(key) {
  const [rows] = await pool.execute('SELECT id AS userId, character FROM users WHERE \`key\` = ?', [key]);
  return rows[0] || null;
}

async function getEventChannels() {
  const [rows] = await pool.execute('SELECT id FROM channels WHERE type = ?', ['event']);
  return rows.map(r => r.id);
}

async function addEventChannel(channelId) {
  await pool.execute('INSERT IGNORE INTO channels (id, type) VALUES (?, ?)', [channelId, 'event']);
}

async function getChatChannels() {
  const [rows] = await pool.execute('SELECT id FROM channels WHERE type = ?', ['chat']);
  return rows.map(r => r.id);
}

async function addChatChannel(channelId) {
  await pool.execute('INSERT IGNORE INTO channels (id, type) VALUES (?, ?)', [channelId, 'chat']);
}

async function saveEvent(event) {
  await pool.execute(
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
  const [rows] = await pool.execute('SELECT * FROM events WHERE channel_id = ?', [channelId]);
  return rows;
}

module.exports = {
  init,
  setKey,
  setCharacter,
  getUserByKey,
  getEventChannels,
  addEventChannel,
  getChatChannels,
  addChatChannel,
  saveEvent,
  getEvents
};
