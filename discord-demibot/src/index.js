const config = require('./config');
const logger = require('./logger');
const db = require('./db');
const discord = require('./discord');
const httpServer = require('./http');

async function start() {
  const missing = [];

  // Validate discord config
  for (const field of ['token', 'clientId', 'apolloBotId']) {
    if (!config.discord || !config.discord[field]) {
      missing.push(`discord.${field}`);
    }
  }

  // Validate database config
  for (const field of ['host', 'user', 'password', 'name']) {
    if (!config.db || !config.db[field]) {
      missing.push(`db.${field}`);
    }
  }

  if (missing.length) {
    logger.error(`Missing configuration values: ${missing.join(', ')}`);
    process.exit(1);
  }

  await db.init(config.db);
  await discord.init(config, db, logger);
  httpServer.start(config, db, discord, logger);
}

start().catch(err => {
  logger.error('Failed to start application', err);
  process.exit(1);
});

