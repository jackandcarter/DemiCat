const express = require('express');
const crypto = require('crypto');

module.exports = ({ discord }) => {
  const router = express.Router();

  router.get('/', (req, res) => {
    const data = discord.embedCache;
    const json = JSON.stringify(data);
    const etag = 'W/"' + crypto.createHash('sha1').update(json).digest('hex') + '"';
    if (req.headers['if-none-match'] === etag) {
      return res.status(304).end();
    }
    res.set('ETag', etag);
    res.type('application/json').send(json);
  });

  return router;
};
