const { Client, GatewayIntentBits, Events, REST, Routes, ActionRowBuilder, StringSelectMenuBuilder, ChannelType } = require('discord.js');
const crypto = require('crypto');
const enqueue = require('../rateLimiter');
const { mapEmbed } = require('./embeds');
const ws = require('../http/ws');

let rest;
let apolloBotId;
let client;

const eventChannels = [];
const fcChatChannels = [];
const officerChatChannels = [];

const embedCache = [];
const messageCache = new Map();

function addToCache(embed) {
  embedCache.push(embed);
  if (embedCache.length > 50) embedCache.shift();
}

function broadcastEmbed(embed) {
  addToCache(embed);
  ws.broadcastEmbed(embed);
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

function trackFcChannel(id) {
  if (!fcChatChannels.includes(id)) fcChatChannels.push(id);
}

function trackOfficerChannel(id) {
  if (!officerChatChannels.includes(id)) officerChatChannels.push(id);
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

function listOnlineUsers() {
  if (!client) return [];
  const users = [];
  client.guilds.cache.forEach(guild => {
    guild.members.cache.forEach(member => {
      if (member.presence?.status === 'online') {
        users.push({ id: member.user.id, name: member.user.username });
      }
    });
  });
  return users;
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
        broadcastEmbed(embed);
      }
    } catch (err) {
      logger.error('Failed to fetch messages for channel', channelId, err);
    }
  }
}

async function fetchInitialMessages(client, logger) {
  for (const channelId of [...fcChatChannels, ...officerChatChannels]) {
    try {
      const channel = await client.channels.fetch(channelId);
      if (!channel?.isTextBased()) continue;
      const messages = await channel.messages.fetch({ limit: 20 });
      const sorted = Array.from(messages.values()).reverse();
      for (const msg of sorted) {
        const mapped = mapMessage(msg);
        addMessage(channelId, mapped);
        ws.broadcastMessage(mapped);
      }
    } catch (err) {
      logger.error('Failed to fetch chat for channel', channelId, err);
    }
  }
}

function getChannelOptions(guild) {
  return guild.channels.cache
    .filter(ch => ch.type === ChannelType.GuildText)
    .map(ch => ({ label: `#${ch.name}`, value: ch.id }))
    .slice(0, 25);
}

async function startDemibotSetupInteraction(interaction) {
  const options = getChannelOptions(interaction.guild);
  const row = new ActionRowBuilder().addComponents(
    new StringSelectMenuBuilder()
      .setCustomId('setup_event')
      .setPlaceholder('Select event channel(s)')
      .setMinValues(1)
      .setMaxValues(Math.min(options.length, 5))
      .addOptions(options)
  );
  await interaction.reply({ content: 'Select event channel(s)', components: [row], ephemeral: true });
}

async function startDemibotSetupGuild(guild) {
  const target = guild.systemChannel || guild.channels.cache.find(c => c.type === ChannelType.GuildText);
  if (!target) return;
  const options = getChannelOptions(guild);
  const row = new ActionRowBuilder().addComponents(
    new StringSelectMenuBuilder()
      .setCustomId('setup_event')
      .setPlaceholder('Select event channel(s)')
      .setMinValues(1)
      .setMaxValues(Math.min(options.length, 5))
      .addOptions(options)
  );
  await target.send({ content: 'Select event channel(s) for DemiCat setup', components: [row] });
}

const commands = [
  { name: 'link', description: 'Link your account' },
  { name: 'createevent', description: 'Create an event' },
  { name: 'generatekey', description: 'Generate a key for DemiCat' },
  { name: 'demibot_setup', description: 'Set up DemiBot in this server' },
  { name: 'demibot_resync', description: 'Resync DemiBot data' },
  { name: 'demibot_embed', description: 'Create a DemiBot embed' },
  { name: 'demibot_reset', description: 'Reset DemiBot data' },
  { name: 'demibot_settings', description: 'View or change DemiBot settings' },
  { name: 'demibot_clear', description: 'Clear DemiBot configuration' }
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
  client = new Client({ intents: [GatewayIntentBits.Guilds, GatewayIntentBits.GuildMessages, GatewayIntentBits.MessageContent, GatewayIntentBits.GuildMembers, GatewayIntentBits.GuildPresences] });
  const token = config.discord.token;
  const clientId = config.discord.clientId;
  if (!token || !clientId) {
    throw new Error('Missing DISCORD_BOT_TOKEN or DISCORD_CLIENT_ID');
  }

  eventChannels.push(...await db.getEventChannels());
  fcChatChannels.push(...await db.getFcChannels());
  officerChatChannels.push(...await db.getOfficerChannels());

  rest = new REST({ version: '10' }).setToken(token);

  client.once(Events.ClientReady, async () => {
    logger.info(`Logged in as ${client.user.tag}`);
    await fetchInitialEmbeds(client, logger);
    await fetchInitialMessages(client, logger);
  });

  client.on(Events.GuildCreate, guild => {
    startDemibotSetupGuild(guild);
  });

  client.on(Events.InteractionCreate, async interaction => {
    if (interaction.isChatInputCommand()) {
      if (interaction.commandName === 'link') {
        await enqueue(() => interaction.reply({ content: 'Link command received', ephemeral: true }));
      } else if (interaction.commandName === 'createevent') {
        await enqueue(() => interaction.reply({ content: 'Create event command received', ephemeral: true }));
      } else if (interaction.commandName === 'generatekey') {
        const key = crypto.randomBytes(16).toString('hex');
        const guildId = interaction.guildId;
        await db.setKey(interaction.user.id, key, guildId);
        await enqueue(() => interaction.reply({ content: 'Sent you a DM with your key!', ephemeral: true }));
        const embed = {
          title: 'DemiCat Link Key',
          fields: [
            { name: 'Key', value: key },
            { name: 'Sync Key', value: guildId }
          ]
        };
        try {
          await enqueue(() => interaction.user.send({ embeds: [embed] }));
        } catch (err) {
          logger.error('Failed to DM key:', err);
        }
      } else if (interaction.commandName === 'demibot_setup') {
        await startDemibotSetupInteraction(interaction);
      }
    } else if (interaction.isStringSelectMenu()) {
      if (interaction.customId === 'setup_event') {
        await db.setServerSettings(interaction.guildId, { eventChannels: interaction.values });
        for (const id of interaction.values) {
          trackEventChannel(id);
        }
        const options = getChannelOptions(interaction.guild);
        const row = new ActionRowBuilder().addComponents(
          new StringSelectMenuBuilder()
            .setCustomId('setup_fc')
            .setPlaceholder('Select Free Company chat channel')
            .setMinValues(1)
            .setMaxValues(1)
            .addOptions(options)
        );
        await interaction.update({ content: 'Select Free Company chat channel', components: [row] });
        await interaction.followUp({ content: 'Saved event channel(s)', ephemeral: true });
      } else if (interaction.customId === 'setup_fc') {
        const channelId = interaction.values[0];
        await db.setServerSettings(interaction.guildId, { fcChatChannel: channelId });
        trackFcChannel(channelId);
        const options = getChannelOptions(interaction.guild);
        const row = new ActionRowBuilder().addComponents(
          new StringSelectMenuBuilder()
            .setCustomId('setup_officer')
            .setPlaceholder('Select officer chat channel')
            .setMinValues(1)
            .setMaxValues(1)
            .addOptions(options)
        );
        await interaction.update({ content: 'Select officer chat channel', components: [row] });
        await interaction.followUp({ content: 'Saved Free Company chat channel', ephemeral: true });
      } else if (interaction.customId === 'setup_officer') {
        const channelId = interaction.values[0];
        await db.setServerSettings(interaction.guildId, { officerChatChannel: channelId });
        trackOfficerChannel(channelId);
        const options = interaction.guild.roles.cache
          .filter(r => r.name !== '@everyone')
          .map(r => ({ label: r.name, value: r.id }))
          .slice(0, 25);
        const row = new ActionRowBuilder().addComponents(
          new StringSelectMenuBuilder()
            .setCustomId('setup_officer_roles')
            .setPlaceholder('Select officer role(s)')
            .setMinValues(1)
            .setMaxValues(Math.min(options.length, 25))
            .addOptions(options)
        );
        await interaction.update({ content: 'Select officer role(s)', components: [row] });
        await interaction.followUp({ content: 'Saved officer chat channel', ephemeral: true });
      } else if (interaction.customId === 'setup_officer_roles') {
        await db.setOfficerRoles(interaction.guildId, interaction.values);
        await interaction.update({ content: 'Setup complete!', components: [] });
        await interaction.followUp({ content: 'Saved officer role(s)', ephemeral: true });
      }
    }
  });

  client.on(Events.MessageCreate, message => {
    if (eventChannels.includes(message.channelId)) {
      if (!(apolloBotId && message.author.id !== apolloBotId) && message.embeds.length > 0) {
        const embed = mapEmbed(message.embeds[0], message);
        broadcastEmbed(embed);
      }
    }
    if (fcChatChannels.includes(message.channelId) || officerChatChannels.includes(message.channelId)) {
      const mapped = mapMessage(message);
      addMessage(message.channelId, mapped);
      ws.broadcastMessage(mapped);
    }
  });

  await registerCommands(clientId, logger);
  await client.login(token);
}

module.exports = {
  init,
  broadcastEmbed,
  mapEmbed,
  addMessage,
  messageCache,
  embedCache,
  getRest: () => rest,
  getClient: () => client,
  trackEventChannel,
  trackFcChannel,
  trackOfficerChannel,
  listOnlineUsers
};

