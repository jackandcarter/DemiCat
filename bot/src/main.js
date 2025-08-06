const { Client, GatewayIntentBits, Events, REST, Routes } = require('discord.js');
const express = require('express');
const crypto = require('crypto');
const http = require('http');
const WebSocket = require('ws');
const db = require('./db');
require('dotenv').config();

const token = process.env.DISCORD_BOT_TOKEN;
const clientId = process.env.DISCORD_CLIENT_ID;

if (!token || !clientId) {
  console.error('Missing DISCORD_BOT_TOKEN or DISCORD_CLIENT_ID');
  process.exit(1);
}

const commands = [
  { name: 'link', description: 'Link your account' },
  { name: 'createevent', description: 'Create an event' },
  { name: 'generatekey', description: 'Generate a key for DemiCat' }
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

const client = new Client({ intents: [GatewayIntentBits.Guilds, GatewayIntentBits.GuildMessages, GatewayIntentBits.MessageContent] });
const apolloBotId = process.env.APOLLO_BOT_ID;
const eventChannels = db.getEventChannels();

// Simple in-memory cache of recent embeds
const embedCache = [];
function addToCache(embed) {
  embedCache.push(embed);
  if (embedCache.length > 50) embedCache.shift();
}

let wss; // will be initialised after server creation

function broadcast(embed) {
  addToCache(embed);
  const data = JSON.stringify(embed);
  if (!wss) return;
  wss.clients.forEach(ws => {
    if (ws.readyState === WebSocket.OPEN) {
      ws.send(data);
    }
  });
}

function mapEmbed(embed, message) {
  const buttons = [];
  if (message.components?.length) {
    for (const row of message.components) {
      for (const comp of row.components) {
        if (comp.type === 2) {
          buttons.push({
            label: comp.label,
            url: comp.url,
            customId: comp.customId
          });
        }
      }
    }
  }

  return {
    id: message.id,
    channelId: message.channelId,
    timestamp: embed.timestamp,
    color: embed.color ?? undefined,
    authorName: embed.author?.name,
    authorIconUrl: embed.author?.iconURL,
    title: embed.title,
    description: embed.description,
    fields: embed.fields.map(f => ({ name: f.name, value: f.value })),
    thumbnailUrl: embed.thumbnail?.url,
    imageUrl: embed.image?.url,
    buttons: buttons.length ? buttons : undefined,
    mentions: message.mentions.users.size > 0 ? Array.from(message.mentions.users.keys()) : undefined
  };
}

async function fetchInitialEmbeds() {
  for (const channelId of eventChannels) {
    try {
      const channel = await client.channels.fetch(channelId);
      if (!channel?.isTextBased()) continue;
      const messages = await channel.messages.fetch({ limit: 10 });
      const sorted = Array.from(messages.values()).reverse();
      for (const msg of sorted) {
        if (apolloBotId && msg.author.id !== apolloBotId) continue;
        if (msg.embeds.length === 0) continue;
        const embed = mapEmbed(msg.embeds[0], msg);
        broadcast(embed);
      }
    } catch (err) {
      console.error('Failed to fetch messages for channel', channelId, err);
    }
  }
}

client.once(Events.ClientReady, async () => {
  console.log(`Logged in as ${client.user.tag}`);
  await fetchInitialEmbeds();
});

client.on(Events.InteractionCreate, async interaction => {
  if (!interaction.isChatInputCommand()) return;

  if (interaction.commandName === 'link') {
    await interaction.reply({ content: 'Link command received', ephemeral: true });
  } else if (interaction.commandName === 'createevent') {
    await interaction.reply({ content: 'Create event command received', ephemeral: true });
  } else if (interaction.commandName === 'generatekey') {
    const key = crypto.randomBytes(16).toString('hex');
    db.setKey(interaction.user.id, key);
    await interaction.reply({ content: 'Sent you a DM with your key!', ephemeral: true });
    const embed = {
      title: 'DemiCat Link Key',
      description: key
    };
    try {
      await interaction.user.send({ embeds: [embed] });
    } catch (err) {
      console.error('Failed to DM key:', err);
    }
  }
});

client.on(Events.MessageCreate, message => {
  if (!eventChannels.includes(message.channelId)) return;
  if (apolloBotId && message.author.id !== apolloBotId) return;
  if (message.embeds.length === 0) return;
  const embed = mapEmbed(message.embeds[0], message);
  broadcast(embed);
});

registerCommands();
client.login(token);

const app = express();
app.use(express.json());

app.post('/plugin', (req, res) => {
  console.log('Plugin connected:', req.body);
  res.status(200).json({ status: 'ok' });
});

app.get('/embeds', (req, res) => {
  res.json(embedCache);
});

app.post('/interactions', async (req, res) => {
  const { messageId, channelId, customId } = req.body;
  try {
    await rest.post('/interactions', {
      body: {
        type: 3,
        channel_id: channelId,
        message_id: messageId,
        application_id: apolloBotId,
        data: { component_type: 2, custom_id: customId }
      }
    });
    res.json({ ok: true });
  } catch (err) {
    console.error('Interaction failed', err);
    res.status(500).json({ ok: false });
  }
});

app.post('/validate', (req, res) => {
  const { key } = req.body;
  const userId = db.getUserIdByKey(key);
  if (userId) {
    res.json({ valid: true, userId });
  } else {
    res.status(401).json({ valid: false });
  }
});

const port = process.env.PLUGIN_PORT || 3000;
const server = http.createServer(app);
wss = new WebSocket.Server({ server });

wss.on('connection', ws => {
  embedCache.forEach(e => ws.send(JSON.stringify(e)));
});

server.listen(port, () => {
  console.log(`Plugin endpoint listening on port ${port}`);
});
