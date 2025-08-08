const express = require('express');
const enqueue = require('../../rateLimiter');

module.exports = ({ db, discord, logger }) => {
  const router = express.Router();
  const client = discord.getClient();

  router.post('/', async (req, res) => {
    const info = req.apiKey;
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
      discord.broadcastEmbed(mapped);
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
      if (logger) logger.error('Event creation failed', err);
      res.status(500).json({ ok: false });
    }
  });

  return router;
};
