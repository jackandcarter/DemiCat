const express = require('express');

module.exports = ({ db, discord }) => {
  const router = express.Router();
  const client = discord.getClient();

  router.post('/', async (req, res) => {
    const { channelId, type } = req.body;
    if (!channelId || !type) {
      return res.status(400).json({ error: 'Missing channelId or type' });
    }
    try {
      const channel = await client.channels.fetch(channelId);
      if (!channel || !channel.isTextBased()) {
        return res.status(400).json({ error: 'Invalid channel' });
      }
      if (type === 'event') {
        await db.addEventChannel(channelId);
        discord.trackEventChannel(channelId);
      } else if (type === 'fc_chat') {
        await db.addFcChannel(channelId);
        discord.trackFcChannel(channelId);
      } else if (type === 'officer_chat') {
        await db.addOfficerChannel(channelId);
        discord.trackOfficerChannel(channelId);
      } else {
        return res.status(400).json({ error: 'Invalid type' });
      }
      res.json({ ok: true });
    } catch (err) {
      res.status(500).json({ ok: false });
    }
  });

  return router;
};
