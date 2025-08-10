const enqueue = require('../rateLimiter');
const discord = require('./index');

async function send(channelId, embed, files = []) {
  const client = discord.getClient();
  const channel = await client.channels.fetch(channelId);
  if (!channel || !channel.isTextBased()) throw new Error('Invalid channel');
  const options = { embeds: [embed] };
  if (files.length) options.files = files;
  const message = await enqueue(() => channel.send(options));
  const mapped = discord.mapEmbed(message.embeds[0], message);
  discord.broadcastEmbed(mapped);
  return message;
}

async function edit(channelId, messageId, embed, files = []) {
  const client = discord.getClient();
  const channel = await client.channels.fetch(channelId);
  if (!channel || !channel.isTextBased()) throw new Error('Invalid channel');
  const message = await channel.messages.fetch(messageId);
  const options = { embeds: [embed] };
  if (files.length) {
    options.files = files;
  } else {
    options.attachments = [];
  }
  const edited = await enqueue(() => message.edit(options));
  const mapped = discord.mapEmbed(edited.embeds[0], edited);
  discord.broadcastEmbed(mapped);
  return edited;
}

module.exports = { send, edit };
