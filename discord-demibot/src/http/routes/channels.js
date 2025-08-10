const express = require('express');

module.exports = ({ db, logger }) => {
  const router = express.Router();

  router.get('/', async (req, res) => {
    try {
      const serverId = req.apiKey.serverId;
      const settings = await db.getServerSettings(serverId);
      const event = settings.eventChannels || [];
      const fc_chat = settings.fcChatChannel ? [settings.fcChatChannel] : [];
      const officer_chat = settings.officerChatChannel ? [settings.officerChatChannel] : [];
      res.json({ event, fc_chat, officer_chat });
    } catch (err) {
      if (logger) logger.error('Failed to fetch channels', err);
      res.status(500).json({ error: 'Failed to fetch channels' });
    }
  });

  return router;
};
