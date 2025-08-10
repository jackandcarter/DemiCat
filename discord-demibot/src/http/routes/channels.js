const express = require('express');

module.exports = ({ db, logger }) => {
  const router = express.Router();

  router.get('/', async (req, res) => {
    try {
      const [event, fc_chat, officer_chat] = await Promise.all([
        db.getEventChannels(),
        db.getFcChannels(),
        db.getOfficerChannels()
      ]);
      res.json({ event, fc_chat, officer_chat });
    } catch (err) {
      if (logger) logger.error('Failed to fetch channels', err);
      res.status(500).json({ error: 'Failed to fetch channels' });
    }
  });

  return router;
};
