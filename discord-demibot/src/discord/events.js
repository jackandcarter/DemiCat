const client = require('./client');

const DISCORD_APOLLO_BOT_ID = process.env.DISCORD_APOLLO_BOT_ID;
const RING_BUFFER_SIZE = 10;

// Map of channelId -> array of latest embeds
const channelEmbeds = new Map();

function getBuffer(channelId) {
  let buf = channelEmbeds.get(channelId);
  if (!buf) {
    buf = [];
    channelEmbeds.set(channelId, buf);
  }
  return buf;
}

function isApollo(message) {
  if (!DISCORD_APOLLO_BOT_ID) return true;
  return message.author && message.author.id === DISCORD_APOLLO_BOT_ID;
}

function mapEmbed(message) {
  const embed = message.embeds[0];
  return {
    id: message.id,
    channelId: message.channelId,
    ...embed.toJSON()
  };
}

function upsert(message) {
  if (!message || !message.embeds || message.embeds.length === 0) return;
  if (!isApollo(message)) return;

  const buf = getBuffer(message.channelId);
  const mapped = mapEmbed(message);
  const idx = buf.findIndex(e => e.id === message.id);
  if (idx !== -1) {
    buf[idx] = mapped;
  } else {
    buf.push(mapped);
    if (buf.length > RING_BUFFER_SIZE) buf.shift();
  }
}

client.on('messageCreate', message => {
  upsert(message);
});

client.on('messageUpdate', (oldMessage, newMessage) => {
  const handle = msg => upsert(msg);
  if (newMessage.partial) {
    newMessage.fetch().then(handle).catch(() => {});
  } else {
    handle(newMessage);
  }
});

async function handleReaction(reaction) {
  try {
    const msg = await reaction.message.fetch();
    upsert(msg);
  } catch (err) {
    // ignore fetch errors
  }
}

client.on('messageReactionAdd', handleReaction);
client.on('messageReactionRemove', handleReaction);

module.exports = {
  channelEmbeds,
  getEmbeds(channelId) {
    return channelEmbeds.get(channelId) || [];
  }
};
