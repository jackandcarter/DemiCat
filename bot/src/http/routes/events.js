const express = require('express');
const send = require('../../discord/send');

module.exports = ({ db, discord, logger }) => {
  const router = express.Router();

  function buildEmbed(data, info, opts = {}) {
    const {
      title,
      description,
      time,
      color,
      url,
      fields,
      thumbnailUrl,
      authorName,
      authorIconUrl
    } = data;
    return {
      title,
      description,
      url,
      timestamp: time ? new Date(time) : undefined,
      color,
      footer: info.character ? { text: info.character } : undefined,
      fields,
      thumbnail: thumbnailUrl ? { url: thumbnailUrl } : undefined,
      author: authorName ? { name: authorName, icon_url: authorIconUrl } : undefined,
      image: opts.image ? { url: 'attachment://image.png' } : undefined
    };
  }

  router.post('/', async (req, res) => {
    const info = req.apiKey;
    const { channelId, title, time, description, imageBase64, color, url, fields, thumbnailUrl, authorName, authorIconUrl } = req.body;
    try {
      const embed = buildEmbed({ title, description, time, color, url, fields, thumbnailUrl, authorName, authorIconUrl }, info, { image: !!imageBase64 });
      const files = imageBase64 ? [{ attachment: Buffer.from(imageBase64, 'base64'), name: 'image.png' }] : [];
      const message = await send.send(channelId, embed, files);
      await db.addEventChannel(channelId);
      discord.trackEventChannel(channelId);
      const id = await db.saveEvent({
        userId: info.userId,
        channelId,
        messageId: message.id,
        title,
        description,
        time,
        metadata: imageBase64 ? 'image' : null
      });
      res.json({ ok: true, id });
    } catch (err) {
      if (logger) logger.error('Event creation failed', err);
      res.status(500).json({ ok: false });
    }
  });

  router.patch('/:id', async (req, res) => {
    const info = req.apiKey;
    const id = parseInt(req.params.id, 10);
    try {
      const existing = await db.getEvent(id);
      if (!existing) return res.status(404).json({ error: 'Not found' });
      if (existing.user_id !== info.userId && !info.isAdmin) return res.status(403).json({ error: 'Forbidden' });
      const title = req.body.title ?? existing.title;
      const description = req.body.description ?? existing.description;
      const time = req.body.time ?? existing.time;
      const imageBase64 = req.body.imageBase64;
      const color = req.body.color;
      const url = req.body.url;
      const fields = req.body.fields;
      const thumbnailUrl = req.body.thumbnailUrl;
      const authorName = req.body.authorName;
      const authorIconUrl = req.body.authorIconUrl;
      const embed = buildEmbed({ title, description, time, color, url, fields, thumbnailUrl, authorName, authorIconUrl }, info, { image: !!imageBase64 });
      const files = imageBase64 ? [{ attachment: Buffer.from(imageBase64, 'base64'), name: 'image.png' }] : [];
      await send.edit(existing.channel_id, existing.message_id, embed, files);
      await db.updateEvent({ id, title, description, time, metadata: imageBase64 ? 'image' : existing.metadata });
      res.json({ ok: true });
    } catch (err) {
      if (logger) logger.error('Event update failed', err);
      res.status(500).json({ ok: false });
    }
  });

  router.delete('/:id', async (req, res) => {
    const info = req.apiKey;
    const id = parseInt(req.params.id, 10);
    try {
      const existing = await db.getEvent(id);
      if (!existing) return res.status(404).json({ error: 'Not found' });
      if (existing.user_id !== info.userId && !info.isAdmin) return res.status(403).json({ error: 'Forbidden' });
      const title = existing.title.endsWith(' (Canceled)') ? existing.title : `${existing.title} (Canceled)`;
      const embed = buildEmbed({ title, description: existing.description, time: existing.time, color: 0x808080 }, info);
      await send.edit(existing.channel_id, existing.message_id, embed, []);
      await db.updateEvent({ id, title, description: existing.description, time: existing.time, metadata: 'canceled' });
      res.json({ ok: true });
    } catch (err) {
      if (logger) logger.error('Event delete failed', err);
      res.status(500).json({ ok: false });
    }
  });

  return router;
};
