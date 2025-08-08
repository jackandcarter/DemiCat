const config = require('./config');
const logger = require('./logger');
const db = require('./db');
const discord = require('./discord');
const httpServer = require('./http');

async function start() {
  await db.init(config.db);
  await discord.init(config, db, logger);
  httpServer.start(config, db, discord, logger);
}

start().catch(err => {
  logger.error('Failed to start application', err);
  process.exit(1);
});

