const { Client, GatewayIntentBits, Events, REST, Routes } = require('discord.js');
const crypto = require('crypto');
const WebSocket = require('ws');
const enqueue = require('../rateLimiter');

let wss;
let rest;
let apolloBotId;
let client;

const eventChannels = [];
const chatChannels = [];

const embedCache = [];
const messageCache = new Map();

function setWss(server) {
  wss = server;
}

function addToCache(embed) {
  embedCache.push(embed);
  if (embedCache.length > 50) embedCache.shift();
}

function broadcast(embed) {
  addToCache(embed);
  if (!wss) return;
  const data = JSON.stringify(embed);
  wss.clients.forEach(ws => {
    if (ws.readyState === WebSocket.OPEN) {
      ws.send(data);
    }
  });
}

function addMessage(channelId, msg) {
  const arr = messageCache.get(channelId) || [];
  arr.push(msg);
  if (arr.length > 50) arr.shift();
  messageCache.set(channelId, arr);
}

function trackEventChannel(id) {
  if (!eventChannels.includes(id)) eventChannels.push(id);
}

function trackChatChannel(id) {
  if (!chatChannels.includes(id)) chatChannels.push(id);
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
    title: embed.title,
    description: embed.description,
    fields: embed.fields.map(f => ({ name: f.name, value: f.value })),
    thumbnailUrl: embed.thumbnail?.url,
    imageUrl: embed.image?.url,
    buttons: buttons.length ? buttons : undefined,
    mentions: message.mentions.users.size > 0 ? Array.from(message.mentions.users.keys()) : undefined
  };
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

async function fetchInitialEmbeds(client, logger) {
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
      logger.error('Failed to fetch messages for channel', channelId, err);
    }
  }
}

async function fetchInitialMessages(client, logger) {
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
      logger.error('Failed to fetch chat for channel', channelId, err);
    }
  }
}

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

async function registerCommands(clientId, logger) {
  try {
    await rest.put(Routes.applicationCommands(clientId), { body: commands });
    logger.info('Successfully registered application commands.');
  } catch (error) {
    logger.error('Error registering commands:', error);
  }
}

async function init(config, db, logger) {
  apolloBotId = config.discord.apolloBotId;
  client = new Client({ intents: [GatewayIntentBits.Guilds, GatewayIntentBits.GuildMessages, GatewayIntentBits.MessageContent] });
  const token = config.discord.token;
  const clientId = config.discord.clientId;
  if (!token || !clientId) {
    throw new Error('Missing DISCORD_BOT_TOKEN or DISCORD_CLIENT_ID');
  }

  eventChannels.push(...await db.getEventChannels());
  chatChannels.push(...await db.getChatChannels());

  rest = new REST({ version: '10' }).setToken(token);

  client.once(Events.ClientReady, async () => {
    logger.info(`Logged in as ${client.user.tag}`);
    await fetchInitialEmbeds(client, logger);
    await fetchInitialMessages(client, logger);
  });

  client.on(Events.InteractionCreate, async interaction => {
    if (!interaction.isChatInputCommand()) return;

    if (interaction.commandName === 'link') {
      await enqueue(() => interaction.reply({ content: 'Link command received', ephemeral: true }));
    } else if (interaction.commandName === 'createevent') {
      await enqueue(() => interaction.reply({ content: 'Create event command received', ephemeral: true }));
    } else if (interaction.commandName === 'generatekey') {
      const key = crypto.randomBytes(16).toString('hex');
      await db.setKey(interaction.user.id, key);
      await enqueue(() => interaction.reply({ content: 'Sent you a DM with your key!', ephemeral: true }));
      const embed = {
        title: 'DemiCat Link Key',
        description: key
      };
      try {
        await enqueue(() => interaction.user.send({ embeds: [embed] }));
      } catch (err) {
        logger.error('Failed to DM key:', err);
      }
    } else if (interaction.commandName === 'setup') {
      const sub = interaction.options.getSubcommand();
      const channel = interaction.options.getChannel('channel');
      if (sub === 'event') {
        await db.addEventChannel(channel.id);
        if (!eventChannels.includes(channel.id)) eventChannels.push(channel.id);
        await enqueue(() => interaction.reply({ content: `Added ${channel} as event channel`, ephemeral: true }));
      } else if (sub === 'chat') {
        await db.addChatChannel(channel.id);
        if (!chatChannels.includes(channel.id)) chatChannels.push(channel.id);
        await enqueue(() => interaction.reply({ content: `Added ${channel} as chat channel`, ephemeral: true }));
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

  await registerCommands(clientId, logger);
  await client.login(token);
}

module.exports = {
  init,
  setWss,
  broadcast,
  mapEmbed,
  addMessage,
  messageCache,
  embedCache,
  getRest: () => rest,
  getClient: () => client,
  trackEventChannel,
  trackChatChannel
};

