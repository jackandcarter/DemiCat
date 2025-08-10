const express = require('express');

module.exports = ({ discord }) => {
  const router = express.Router();

  router.get('/', (req, res) => {
    const users = discord.listOnlineUsers();
    res.json(users);
  });

  return router;
};

