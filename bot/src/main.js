const { Client, GatewayIntentBits, Events, REST, Routes } = require('discord.js');
const express = require('express');
const crypto = require('crypto');
const http = require('http');
const WebSocket = require('ws');
const fs = require('fs');
const path = require('path');
const readline = require('readline/promises');
const { stdin: input, stdout: output } = require('process');
const db = require('./db');

const envPath = path.join(__dirname, '..', '.env');
require('dotenv').config({ path: envPath });

async function ensureEnv() {
  const rl = readline.createInterface({ input, output });
  const keys = ['DISCORD_BOT_TOKEN', 'DISCORD_CLIENT_ID', 'DB_HOST', 'DB_USER', 'DB_PASSWORD', 'DB_NAME'];
  const added = [];
  for (const key of keys) {
    if (!process.env[key]) {
      const val = await rl.question(`${key}: `);
      process.env[key] = val;
      added.push(`${key}=${val}`);
    }
  }
  rl.close();
  if (added.length) {
    const existing = fs.existsSync(envPath) ? fs.readFileSync(envPath, 'utf8') : '';
    const newline = existing.endsWith('\n') || existing.length === 0 ? '' : '\n';
    fs.writeFileSync(envPath, existing + newline + added.join('\n') + '\n');
    console.log('Configuration saved to .env');
  }
}

let eventChannels = [];
let chatChannels = [];
let rest;
const client = new Client({ intents: [GatewayIntentBits.Guilds, GatewayIntentBits.GuildMessages, GatewayIntentBits.MessageContent] });
const apolloBotId = process.env.APOLLO_BOT_ID;

const commands = [
  { name: 'link', description: 'Link your account' },
  { name: 'createevent', description: 'Create an event' },
  { name: 'generatekey', description: 'Generate a key for DemiCat' },
  {
    name: 'setup',
    description: 'Configure DemiCat channels',
    options: [
      {
        type: 1,
        name: 'event',
        description: 'Add an event channel',
        options: [
          { type: 7, name: 'channel', description: 'Channel to use', required: true }
        ]
      },
      {
        type: 1,
        name: 'chat',
        description: 'Add a chat channel',
        options: [
          { type: 7, name: 'channel', description: 'Channel to use', required: true }
        ]
      }
    ]
  }
];

async function registerCommands(clientId) {
  try {
    await rest.put(Routes.applicationCommands(clientId), { body: commands });
    console.log('Successfully registered application commands.');
  } catch (error) {
    console.error('Error registering commands:', error);
  }
}

// Simple in-memory cache of recent embeds
const embedCache = [];
function addToCache(embed) {
  embedCache.push(embed);
  if (embedCache.length > 50) embedCache.shift();
}

const messageCache = new Map();
function addMessage(channelId, msg) {
  const arr = messageCache.get(channelId) || [];
  arr.push(msg);
  if (arr.length > 50) arr.shift();
  messageCache.set(channelId, arr);
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

function mapMessage(message) {
  return {
    id: message.id,
    channelId: message.channelId,
    authorId: message.author.id,
    authorName: message.author.username,
    content: message.content,
    mentions: Array.from(message.mentions.users.values()).map(u => ({ id: u.id, name: u.username })),
    timestamp: message.createdTimestamp
  };
}

async function fetchInitialMessages() {
  for (const channelId of chatChannels) {
    try {
      const channel = await client.channels.fetch(channelId);
      if (!channel?.isTextBased()) continue;
      const messages = await channel.messages.fetch({ limit: 20 });
      const sorted = Array.from(messages.values()).reverse();
      for (const msg of sorted) {
        const mapped = mapMessage(msg);
        addMessage(channelId, mapped);
      }
    } catch (err) {
      console.error('Failed to fetch chat for channel', channelId, err);
    }
  }
}

client.once(Events.ClientReady, async () => {
  console.log(`Logged in as ${client.user.tag}`);
  await fetchInitialEmbeds();
  await fetchInitialMessages();
});

client.on(Events.InteractionCreate, async interaction => {
  if (!interaction.isChatInputCommand()) return;

  if (interaction.commandName === 'link') {
    await interaction.reply({ content: 'Link command received', ephemeral: true });
  } else if (interaction.commandName === 'createevent') {
    await interaction.reply({ content: 'Create event command received', ephemeral: true });
  } else if (interaction.commandName === 'generatekey') {
    const key = crypto.randomBytes(16).toString('hex');
    await db.setKey(interaction.user.id, key);
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
  } else if (interaction.commandName === 'setup') {
    const sub = interaction.options.getSubcommand();
    const channel = interaction.options.getChannel('channel');
    if (sub === 'event') {
      await db.addEventChannel(channel.id);
      if (!eventChannels.includes(channel.id)) eventChannels.push(channel.id);
      await interaction.reply({ content: `Added ${channel} as event channel`, ephemeral: true });
    } else if (sub === 'chat') {
      await db.addChatChannel(channel.id);
      if (!chatChannels.includes(channel.id)) chatChannels.push(channel.id);
      await interaction.reply({ content: `Added ${channel} as chat channel`, ephemeral: true });
    }
  }
});

client.on(Events.MessageCreate, message => {
  if (eventChannels.includes(message.channelId)) {
    if (!(apolloBotId && message.author.id !== apolloBotId) && message.embeds.length > 0) {
      const embed = mapEmbed(message.embeds[0], message);
      broadcast(embed);
    }
  }
  if (chatChannels.includes(message.channelId)) {
    const mapped = mapMessage(message);
    addMessage(message.channelId, mapped);
  }
});

const app = express();
app.use(express.json());

app.post('/plugin', (req, res) => {
  console.log('Plugin connected:', req.body);
  res.status(200).json({ status: 'ok' });
});

app.get('/embeds', (req, res) => {
  res.json(embedCache);
});

app.get('/channels', async (req, res) => {
  try {
    const [event, chat] = await Promise.all([
      db.getEventChannels(),
      db.getChatChannels()
    ]);
    res.json({ event, chat });
  } catch (err) {
    console.error('Failed to fetch channels', err);
    res.status(500).json({ error: 'Failed to fetch channels' });
  }
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

app.post('/validate', async (req, res) => {
  const { key, characterName } = req.body;
  const info = await db.getUserByKey(key);
  if (info) {
    if (characterName) {
      await db.setCharacter(info.userId, characterName);
    }
    res.json({ valid: true, userId: info.userId });
  } else {
    res.status(401).json({ valid: false });
  }
});

app.post('/events', async (req, res) => {
  const auth = req.headers.authorization || '';
  if (!auth.startsWith('Bearer ')) {
    return res.status(401).json({ error: 'Unauthorized' });
  }
  const key = auth.substring(7);
  const info = await db.getUserByKey(key);
  if (!info) {
    return res.status(401).json({ error: 'Invalid key' });
  }

  const { channelId, title, time, description, imageBase64 } = req.body;
  try {
    const channel = await client.channels.fetch(channelId);
    if (!channel || !channel.isTextBased()) {
      return res.status(400).json({ error: 'Invalid channel' });
    }
    const embed = {
      title,
      description,
      timestamp: time ? new Date(time) : undefined,
      footer: info.character ? { text: info.character } : undefined,
      image: imageBase64 ? { url: 'attachment://image.png' } : undefined
    };
    const files = imageBase64 ? [{ attachment: Buffer.from(imageBase64, 'base64'), name: 'image.png' }] : [];
    const message = await channel.send({ embeds: [embed], files });
    const mapped = mapEmbed(message.embeds[0], message);
    broadcast(mapped);
    await db.addEventChannel(channelId);
    await db.saveEvent({
      userId: info.userId,
      channelId,
      messageId: message.id,
      title,
      description,
      time,
      metadata: imageBase64 ? 'image' : null
    });
    res.json({ ok: true });
  } catch (err) {
    console.error('Event creation failed', err);
    res.status(500).json({ ok: false });
  }
});

app.get('/messages/:channelId', (req, res) => {
  res.json(messageCache.get(req.params.channelId) || []);
});

app.post('/messages', async (req, res) => {
  const auth = req.headers.authorization || '';
  if (!auth.startsWith('Bearer ')) {
    return res.status(401).json({ error: 'Unauthorized' });
  }
  const key = auth.substring(7);
  const info = await db.getUserByKey(key);
  if (!info) {
    return res.status(401).json({ error: 'Invalid key' });
  }

  const { channelId, content, useCharacterName } = req.body;
  try {
    const channel = await client.channels.fetch(channelId);
    if (!channel || !channel.isTextBased()) {
      return res.status(400).json({ error: 'Invalid channel' });
    }
    const user = await client.users.fetch(info.userId);
    const displayName = useCharacterName && info.character ? info.character : user.username;
    const hooks = await channel.fetchWebhooks();
    let hook = hooks.find(w => w.name === 'DemiCat');
    if (!hook) {
      hook = await channel.createWebhook({ name: 'DemiCat' });
    }
    await hook.send({ content, username: displayName });
    await db.addChatChannel(channelId);
    if (!chatChannels.includes(channelId)) {
      chatChannels.push(channelId);
    }
    res.json({ ok: true });
  } catch (err) {
    console.error('Message send failed', err);
    res.status(500).json({ ok: false });
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

async function start() {
  await ensureEnv();
  const token = process.env.DISCORD_BOT_TOKEN;
  const clientId = process.env.DISCORD_CLIENT_ID;
  if (!token || !clientId) {
    console.error('Missing DISCORD_BOT_TOKEN or DISCORD_CLIENT_ID');
    process.exit(1);
  }
  await db.init();
  eventChannels = await db.getEventChannels();
  chatChannels = await db.getChatChannels();
  rest = new REST({ version: '10' }).setToken(token);
  await registerCommands(clientId);
  await client.login(token);
}

start();
