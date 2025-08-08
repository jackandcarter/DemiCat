const express = require('express');

module.exports = ({ db, logger }) => {
  const router = express.Router();

  router.get('/', async (req, res) => {
    try {
      const [event, chat] = await Promise.all([
        db.getEventChannels(),
        db.getChatChannels()
      ]);
      res.json({ event, chat });
    } catch (err) {
      if (logger) logger.error('Failed to fetch channels', err);
      res.status(500).json({ error: 'Failed to fetch channels' });
    }
  });

  return router;
};
