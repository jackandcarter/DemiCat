const { Client, GatewayIntentBits, Events, REST, Routes } = require('discord.js');
const express = require('express');
require('dotenv').config();

const token = process.env.DISCORD_BOT_TOKEN;
const clientId = process.env.DISCORD_CLIENT_ID;

if (!token || !clientId) {
  console.error('Missing DISCORD_BOT_TOKEN or DISCORD_CLIENT_ID');
  process.exit(1);
}

const commands = [
  { name: 'link', description: 'Link your account' },
  { name: 'createevent', description: 'Create an event' }
];

const rest = new REST({ version: '10' }).setToken(token);

async function registerCommands() {
  try {
    await rest.put(Routes.applicationCommands(clientId), { body: commands });
    console.log('Successfully registered application commands.');
  } catch (error) {
    console.error('Error registering commands:', error);
  }
}

const client = new Client({ intents: [GatewayIntentBits.Guilds] });

client.once(Events.ClientReady, () => {
  console.log(`Logged in as ${client.user.tag}`);
});

client.on(Events.InteractionCreate, async interaction => {
  if (!interaction.isChatInputCommand()) return;

  if (interaction.commandName === 'link') {
    await interaction.reply({ content: 'Link command received', ephemeral: true });
  } else if (interaction.commandName === 'createevent') {
    await interaction.reply({ content: 'Create event command received', ephemeral: true });
  }
});

registerCommands();
client.login(token);

const app = express();
app.use(express.json());

app.post('/plugin', (req, res) => {
  console.log('Plugin connected:', req.body);
  res.status(200).json({ status: 'ok' });
});

const port = process.env.PLUGIN_PORT || 3000;
app.listen(port, () => {
  console.log(`Plugin endpoint listening on port ${port}`);
});
