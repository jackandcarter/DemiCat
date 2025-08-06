const path = require('path');
const Database = require('better-sqlite3');

const dbPath = path.join(__dirname, '..', '..', 'database', 'demicat.db');
const db = new Database(dbPath);

db.exec(`
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
`);

function setKey(userId, key) {
  db.prepare('INSERT INTO users (id, key) VALUES (?, ?) ON CONFLICT(id) DO UPDATE SET key=excluded.key').run(userId, key);
}

function setCharacter(userId, character) {
  db.prepare('INSERT INTO users (id, character) VALUES (?, ?) ON CONFLICT(id) DO UPDATE SET character=excluded.character').run(userId, character);
}

function getUserByKey(key) {
  return db.prepare('SELECT id as userId, character FROM users WHERE key = ?').get(key) || null;
}

function getEventChannels() {
  return db.prepare('SELECT id FROM channels WHERE type = ?').all('event').map(r => r.id);
}

function addEventChannel(channelId) {
  db.prepare('INSERT OR IGNORE INTO channels (id, type) VALUES (?, ?)').run(channelId, 'event');
}

function getChatChannels() {
  return db.prepare('SELECT id FROM channels WHERE type = ?').all('chat').map(r => r.id);
}

function addChatChannel(channelId) {
  db.prepare('INSERT OR IGNORE INTO channels (id, type) VALUES (?, ?)').run(channelId, 'chat');
}

function saveEvent(event) {
  db.prepare('INSERT INTO events (user_id, channel_id, message_id, title, description, time, metadata) VALUES (?, ?, ?, ?, ?, ?, ?)')
    .run(event.userId, event.channelId, event.messageId, event.title, event.description, event.time, event.metadata);
}

function getEvents(channelId) {
  return db.prepare('SELECT * FROM events WHERE channel_id = ?').all(channelId);
}

module.exports = {
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
