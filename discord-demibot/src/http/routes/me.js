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
    const info = req.apiKey;
    if (!userId || userId !== info.userId) {
      return res.status(403).json({ hasOfficerRole: false });
    }
    try {
      const guild = client.guilds.cache.get(info.serverId);
      if (!guild) {
        return res.status(500).json({ hasOfficerRole: false });
      }
      const member = await guild.members.fetch(userId);
      const roles = Array.from(member.roles.cache.keys());
      const officerRoles = await db.getOfficerRoles(info.serverId);
      const hasOfficerRole = roles.some(r => officerRoles.includes(r));
      res.json({ hasOfficerRole });
    } catch (err) {
      res.status(500).json({ hasOfficerRole: false });
    }
  });

  return router;
};
