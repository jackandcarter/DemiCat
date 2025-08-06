const fs = require('fs');
const path = require('path');

const filePath = path.join(__dirname, '..', '..', 'database', 'users.json');

function read() {
  try {
    const data = fs.readFileSync(filePath, 'utf8');
    return JSON.parse(data || '{}');
  } catch (err) {
    return {};
  }
}

function write(data) {
  fs.writeFileSync(filePath, JSON.stringify(data, null, 2));
}

function setKey(userId, key) {
  const data = read();
  data[userId] = key;
  write(data);
}

function getUserIdByKey(key) {
  const data = read();
  for (const [id, storedKey] of Object.entries(data)) {
    if (storedKey === key) {
      return id;
    }
  }
  return null;
}

module.exports = { setKey, getUserIdByKey };
