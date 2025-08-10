const APOLLO_BOT_ID = process.env.DISCORD_APOLLO_BOT_ID;

// Emojis commonly used by Apollo for RSVP
const RSVP_EMOJIS = ['✅', '❌', '❓', '❔', '🤷'];

function isApolloEmbed(message) {
  if (!message) return false;
  if (APOLLO_BOT_ID && message.author?.id !== APOLLO_BOT_ID) return false;
  const embed = message.embeds && message.embeds[0];
  if (!embed) return false;
  const footer = embed.footer?.text?.toLowerCase() || '';
  if (footer.includes('apollo')) return true;
  // Fallback: look for RSVP emojis in footer or reactions
  if (RSVP_EMOJIS.some(e => footer.includes(e))) return true;
  if (message.reactions && message.reactions.cache) {
    for (const r of message.reactions.cache.values()) {
      if (RSVP_EMOJIS.includes(r.emoji.name)) return true;
    }
  }
  return true; // assume true if bot id matches
}

function extractButtons(message, embed) {
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

  if (buttons.length === 0) {
    // attempt to create RSVP buttons from reactions or footer text
    const counts = {};
    if (message.reactions?.cache?.size) {
      for (const [key, r] of message.reactions.cache) {
        if (RSVP_EMOJIS.includes(r.emoji.name)) {
          counts[r.emoji.name] = r.count;
        }
      }
    }
    const footerText = embed.footer?.text || '';
    for (const emoji of RSVP_EMOJIS) {
      if (counts[emoji] === undefined) {
        const regex = new RegExp(`${emoji}\\s*(\\d+)`);
        const match = footerText.match(regex);
        if (match) counts[emoji] = parseInt(match[1], 10);
      }
    }
    for (const emoji of Object.keys(counts)) {
      buttons.push({ label: `${emoji} ${counts[emoji]}`, customId: emoji });
    }
  }

  return buttons.length ? buttons : undefined;
}

function mapEmbed(embed, message) {
  if (!embed || !message) return null;
  const dto = {
    id: message.id,
    channelId: message.channelId,
    timestamp: embed.timestamp ? new Date(embed.timestamp).toISOString() : undefined,
    color: embed.color ?? undefined,
    authorName: embed.author?.name,
    authorIconUrl: embed.author?.iconURL || embed.author?.icon_url,
    title: embed.title,
    description: embed.description,
    fields: embed.fields?.length ? embed.fields.map(f => ({ name: f.name, value: f.value })) : undefined,
    thumbnailUrl: embed.thumbnail?.url,
    imageUrl: embed.image?.url,
    buttons: extractButtons(message, embed),
    mentions: message.mentions?.users?.size > 0 ? Array.from(message.mentions.users.values()).map(u => u.username) : undefined
  };
  return dto;
}

module.exports = {
  mapEmbed,
  isApolloEmbed,
};

