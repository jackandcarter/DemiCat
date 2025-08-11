const fs = require('fs');
const path = require('path');
const dotenv = require('dotenv');

const envPath = path.join(__dirname, '..', '.env');
dotenv.config({ path: envPath });

function requireEnv(key, prompt) {
  let value = process.env[key];

  while (!value || value.trim() === '') {
    fs.writeSync(process.stdout.fd, `${prompt}: `);
    const buf = Buffer.alloc(1);
    let input = '';
    while (true) {
      const bytes = fs.readSync(process.stdin.fd, buf, 0, 1);
      if (!bytes) return '';
      const ch = buf.toString();
      if (ch === '\n') break;
      input += ch;
    }
    value = input.trim();
  }

  process.env[key] = value;
  fs.appendFileSync(envPath, `${key}=${value}\n`);

  return value;
}

requireEnv('DISCORD_BOT_TOKEN', 'Enter Discord bot token');
requireEnv('DISCORD_CLIENT_ID', 'Enter Discord client ID');
requireEnv('APOLLO_BOT_ID', 'Enter Apollo bot ID');
requireEnv('DB_HOST', 'Enter database host');
requireEnv('DB_USER', 'Enter database user');
requireEnv('DB_PASSWORD', 'Enter database password');
requireEnv('DB_NAME', 'Enter database name');
requireEnv('PLUGIN_PORT', 'Enter plugin port');

module.exports = {
  port: process.env.PLUGIN_PORT || 3000,
  discord: {
    token: process.env.DISCORD_BOT_TOKEN,
    clientId: process.env.DISCORD_CLIENT_ID,
    apolloBotId: process.env.APOLLO_BOT_ID
  },
  db: {
    host: process.env.DB_HOST,
    user: process.env.DB_USER,
    password: process.env.DB_PASSWORD,
    name: process.env.DB_NAME
  }
};

