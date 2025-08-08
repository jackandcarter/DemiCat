const express = require('express');
const http = require('http');
const WebSocket = require('ws');
const enqueue = require('../rateLimiter');

function start(config, db, discord, logger) {
  const client = discord.getClient();
  const rest = discord.getRest();

  const app = express();
  app.use(express.json());

  app.post('/plugin', (req, res) => {
    logger.info('Plugin connected:', req.body);
    res.status(200).json({ status: 'ok' });
  });

  app.get('/embeds', (req, res) => {
    res.json(discord.embedCache);
  });

  app.get('/channels', async (req, res) => {
    try {
      const [event, chat] = await Promise.all([
        db.getEventChannels(),
        db.getChatChannels()
      ]);
      res.json({ event, chat });
    } catch (err) {
      logger.error('Failed to fetch channels', err);
      res.status(500).json({ error: 'Failed to fetch channels' });
    }
  });

  app.post('/interactions', async (req, res) => {
    const { messageId, channelId, customId } = req.body;
    try {
      await enqueue(() => rest.post('/interactions', {
        body: {
          type: 3,
          channel_id: channelId,
          message_id: messageId,
          application_id: config.discord.apolloBotId,
          data: { component_type: 2, custom_id: customId }
        }
      }));
      res.json({ ok: true });
    } catch (err) {
      logger.error('Interaction failed', err);
      res.status(500).json({ ok: false });
    }
  });

  app.post('/validate', async (req, res) => {
    const { key, characterName } = req.body;
    const info = await db.getUserByKey(key);
    if (info) {
      if (characterName) {
        await db.setCharacter(info.userId, characterName);
      }
      res.json({ valid: true, userId: info.userId });
    } else {
      res.status(401).json({ valid: false });
    }
  });

  app.post('/events', async (req, res) => {
    const auth = req.headers.authorization || '';
    if (!auth.startsWith('Bearer ')) {
      return res.status(401).json({ error: 'Unauthorized' });
    }
    const key = auth.substring(7);
    const info = await db.getUserByKey(key);
    if (!info) {
      return res.status(401).json({ error: 'Invalid key' });
    }

    const { channelId, title, time, description, imageBase64 } = req.body;
    try {
      const channel = await client.channels.fetch(channelId);
      if (!channel || !channel.isTextBased()) {
        return res.status(400).json({ error: 'Invalid channel' });
      }
      const embed = {
        title,
        description,
        timestamp: time ? new Date(time) : undefined,
        footer: info.character ? { text: info.character } : undefined,
        image: imageBase64 ? { url: 'attachment://image.png' } : undefined
      };
      const files = imageBase64 ? [{ attachment: Buffer.from(imageBase64, 'base64'), name: 'image.png' }] : [];
      const message = await enqueue(() => channel.send({ embeds: [embed], files }));
      const mapped = discord.mapEmbed(message.embeds[0], message);
      discord.broadcast(mapped);
      await db.addEventChannel(channelId);
      discord.trackEventChannel(channelId);
      await db.saveEvent({
        userId: info.userId,
        channelId,
        messageId: message.id,
        title,
        description,
        time,
        metadata: imageBase64 ? 'image' : null
      });
      res.json({ ok: true });
    } catch (err) {
      logger.error('Event creation failed', err);
      res.status(500).json({ ok: false });
    }
  });

  app.get('/messages/:channelId', (req, res) => {
    res.json(discord.messageCache.get(req.params.channelId) || []);
  });

  app.post('/messages', async (req, res) => {
    const auth = req.headers.authorization || '';
    if (!auth.startsWith('Bearer ')) {
      return res.status(401).json({ error: 'Unauthorized' });
    }
    const key = auth.substring(7);
    const info = await db.getUserByKey(key);
    if (!info) {
      return res.status(401).json({ error: 'Invalid key' });
    }

    const { channelId, content, useCharacterName } = req.body;
    try {
      const channel = await client.channels.fetch(channelId);
      if (!channel || !channel.isTextBased()) {
        return res.status(400).json({ error: 'Invalid channel' });
      }
      const user = await client.users.fetch(info.userId);
      const displayName = useCharacterName && info.character ? info.character : user.username;
      const hooks = await channel.fetchWebhooks();
      let hook = hooks.find(w => w.name === 'DemiCat');
      if (!hook) {
        hook = await channel.createWebhook({ name: 'DemiCat' });
      }
      await enqueue(() => hook.send({ content, username: displayName }));
      await db.addChatChannel(channelId);
      discord.trackChatChannel(channelId);
      res.json({ ok: true });
    } catch (err) {
      logger.error('Message send failed', err);
      res.status(500).json({ ok: false });
    }
  });

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

