const fs = require('fs');
const path = require('path');

const userFile = path.join(__dirname, '..', '..', 'database', 'users.json');
const serverFile = path.join(__dirname, '..', '..', 'database', 'servers.json');

function read(file) {
  try {
    const data = fs.readFileSync(file, 'utf8');
    return JSON.parse(data || '{}');
  } catch (err) {
    return {};
  }
}

function write(file, data) {
  fs.writeFileSync(file, JSON.stringify(data, null, 2));
}

function setKey(userId, key) {
  const data = read(userFile);
  const existing = data[userId] || {};
  data[userId] = { ...existing, key };
  write(userFile, data);
}

function setCharacter(userId, character) {
  const data = read(userFile);
  const existing = data[userId] || {};
  data[userId] = { ...existing, character };
  write(userFile, data);
}

function getUserByKey(key) {
  const data = read(userFile);
  for (const [id, info] of Object.entries(data)) {
    if (info.key === key) {
      return { userId: id, character: info.character };
    }
  }
  return null;
}

function getEventChannels() {
  const data = read(serverFile);
  return Array.isArray(data.eventChannels) ? data.eventChannels : [];
}

function addEventChannel(channelId) {
  const data = read(serverFile);
  data.eventChannels = Array.isArray(data.eventChannels) ? data.eventChannels : [];
  if (!data.eventChannels.includes(channelId)) {
    data.eventChannels.push(channelId);
    write(serverFile, data);
  }
}

module.exports = { setKey, setCharacter, getUserByKey, getEventChannels, addEventChannel };
