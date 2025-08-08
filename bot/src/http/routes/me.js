const express = require('express');

module.exports = ({ db, discord }) => {
  const router = express.Router();
  const client = discord.getClient();

  router.get('/', (req, res) => {
    const info = req.apiKey || {};
    res.json({ userId: info.userId, isAdmin: !!info.isAdmin });
  });

  router.get('/roles', async (req, res) => {
    const { userId } = req.query;
    if (!userId) {
      return res.status(400).json({ error: 'Missing userId' });
    }
    try {
      const guild = client.guilds.cache.first();
      if (!guild) {
        return res.status(500).json({ hasOfficerRole: false });
      }
      const member = await guild.members.fetch(userId);
      const roles = Array.from(member.roles.cache.keys());
      const officerRoles = await db.getOfficerRoles();
      const hasOfficerRole = roles.some(r => officerRoles.includes(r));
      res.json({ hasOfficerRole });
    } catch (err) {
      res.status(500).json({ hasOfficerRole: false });
    }
  });

  return router;
};
