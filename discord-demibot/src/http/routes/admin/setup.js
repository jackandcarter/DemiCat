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
      const guildId = req.apiKey.serverId;
      if (type === 'event') {
        const settings = await db.getServerSettings(guildId);
        const events = new Set(settings.eventChannels || []);
        events.add(channelId);
        await db.setServerSettings(guildId, { eventChannels: Array.from(events) });
        discord.trackEventChannel(channelId);
      } else if (type === 'fc_chat') {
        await db.setServerSettings(guildId, { fcChatChannel: channelId });
        discord.trackFcChannel(channelId);
      } else if (type === 'officer_chat') {
        await db.setServerSettings(guildId, { officerChatChannel: channelId });
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
