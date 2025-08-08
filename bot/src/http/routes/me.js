const express = require('express');

module.exports = () => {
  const router = express.Router();

  router.get('/', (req, res) => {
    const info = req.apiKey || {};
    res.json({ userId: info.userId, isAdmin: !!info.isAdmin });
  });

  return router;
};
