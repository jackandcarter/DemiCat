const express = require('express');
const crypto = require('crypto');
const enqueue = require('../../rateLimiter');

module.exports = ({ db, discord, logger }) => {
  const router = express.Router();
  const client = discord.getClient();

  router.get('/:channelId', (req, res) => {
    const arr = discord.messageCache.get(req.params.channelId) || [];
    const json = JSON.stringify(arr);
    const etag = 'W/"' + crypto.createHash('sha1').update(json).digest('hex') + '"';
    if (req.headers['if-none-match'] === etag) {
      return res.status(304).end();
    }
    res.set('ETag', etag);
    res.type('application/json').send(json);
  });

  router.post('/', async (req, res) => {
    const { channelId, content, useCharacterName } = req.body;
    const info = req.apiKey;
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
      await db.addFcChannel(channelId);
      discord.trackFcChannel(channelId);
      res.json({ ok: true });
    } catch (err) {
      if (logger) logger.error('Message send failed', err);
      res.status(500).json({ ok: false });
    }
  });

  return router;
};
