const express = require('express');
const http = require('http');
const WebSocket = require('ws');

function start(config, db, discord, logger) {
  const app = express();
  app.set('etag', false);
  app.use(express.json());

  // CORS for localhost and Dalamud origin
  const DALAMUD_ORIGIN = 'app://dalamud';
  app.use((req, res, next) => {
    const origin = req.headers.origin;
    if (origin && (origin.startsWith('http://localhost:') || origin === DALAMUD_ORIGIN)) {
      res.setHeader('Access-Control-Allow-Origin', origin);
      res.setHeader('Vary', 'Origin');
      res.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-Api-Key');
      res.setHeader('Access-Control-Allow-Methods', 'GET,POST,OPTIONS');
    }
    if (req.method === 'OPTIONS') {
      return res.sendStatus(200);
    }
    next();
  });

  // Request logging
  app.use((req, res, next) => {
    logger.info(`${req.method} ${req.url}`);
    next();
  });

  // X-Api-Key middleware
  app.use('/api', async (req, res, next) => {
    try {
      const key = req.get('X-Api-Key');
      if (!key) {
        return res.status(401).json({ error: 'Missing X-Api-Key' });
      }
      const info = await db.getApiKey(key);
      if (!info) {
        return res.status(401).json({ error: 'Invalid API key' });
      }
      req.apiKey = info;
      next();
    } catch (err) {
      next(err);
    }
  });

  // Admin guard
  app.use('/api/admin', (req, res, next) => {
    if (!req.apiKey?.isAdmin) {
      return res.status(403).json({ error: 'Forbidden' });
    }
    next();
  });

  // Routes
  const channels = require('./routes/channels');
  const messages = require('./routes/messages');
  const embeds = require('./routes/embeds');
  const events = require('./routes/events');
  const me = require('./routes/me');
  const adminSetup = require('./routes/admin/setup');

  app.use('/api/channels', channels({ db, logger }));
  app.use('/api/messages', messages({ db, discord, logger }));
  app.use('/api/embeds', embeds({ discord }));
  app.use('/api/events', events({ db, discord, logger }));
  app.use('/api/me', me());
  app.use('/api/admin/setup', adminSetup({ db, discord }));

  const server = http.createServer(app);
  const wss = new WebSocket.Server({ server });

  wss.on('connection', ws => {
    discord.embedCache.forEach(e => ws.send(JSON.stringify(e)));
  });

  server.listen(config.port, () => {
    logger.info(`Plugin endpoint listening on port ${config.port}`);
  });

  return { app, server, wss };
}

module.exports = { start };
