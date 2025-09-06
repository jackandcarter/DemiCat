<template>
  <div class="embed" :style="colorStyle">
    <div class="embed-content">
      <div v-if="embed.title" class="embed-title">
        <a v-if="embed.url" :href="embed.url" target="_blank" rel="noopener">{{ embed.title }}</a>
        <span v-else>{{ embed.title }}</span>
      </div>
      <div v-if="embed.description" class="embed-description" v-html="embed.description" />
      <div v-if="embed.fields && embed.fields.length" class="embed-fields">
        <div v-for="(field, i) in embed.fields" :key="i" class="embed-field" :class="{ inline: field.inline }">
          <div class="field-name" v-if="field.name">{{ field.name }}</div>
          <div class="field-value" v-if="field.value" v-html="field.value" />
        </div>
      </div>
      <div v-if="embed.image" class="embed-image">
        <img :src="embed.image.url" alt="" />
      </div>
      <div v-if="embed.thumbnail" class="embed-thumbnail">
        <img :src="embed.thumbnail.url" alt="" />
      </div>
      <div v-if="embed.footer" class="embed-footer">
        <span v-if="embed.footer.icon_url" class="footer-icon">
          <img :src="embed.footer.icon_url" alt="" />
        </span>
        <span class="footer-text">{{ embed.footer.text }}</span>
      </div>
      <div v-if="embed.buttons && embed.buttons.length" class="embed-buttons">
        <button
          v-for="(btn, i) in embed.buttons"
          :key="i"
          @click="rsvp(btn.customId)"
        >
          <span v-if="btn.emoji">{{ btn.emoji }} </span>{{ btn.label }}
        </button>
      </div>
    </div>
  </div>
</template>

<script>
export default {
  name: 'EmbedRenderer',
  props: {
    embed: {
      type: Object,
      required: true
    },
    eventId: {
      type: String,
      required: false
    }
  },
  computed: {
    colorStyle() {
      if (!this.embed.color) return {};
      const color = `#${this.embed.color.toString(16).padStart(6, '0')}`;
      return { borderColor: color };
    }
  },
  methods: {
    async rsvp(customId) {
      const tag = customId.includes(':') ? customId.split(':', 2)[1] : customId;
      try {
        await fetch(`/api/events/${this.eventId || this.embed.id}/rsvp`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ tag })
        });
      } catch (e) {
        console.error('RSVP failed', e);
      }
    }
  }
};
</script>

<style scoped>
.embed {
  display: flex;
  border-left: 4px solid #ccc;
  background-color: #2f3136;
  padding: 10px;
  border-radius: 4px;
  color: #dcddde;
  max-width: 500px;
}
.embed-content {
  flex: 1;
}
.embed-title a,
.embed-title span {
  font-weight: 600;
  color: #00aff4;
  text-decoration: none;
}
.embed-description {
  margin-top: 4px;
  white-space: pre-line;
}
.embed-fields {
  display: flex;
  flex-wrap: wrap;
  margin-top: 8px;
}
.embed-field {
  flex: 1 1 100%;
  margin-bottom: 8px;
}
.embed-field.inline {
  flex: 1 1 45%;
  margin-right: 5%;
}
.embed-field.inline:nth-child(2n) {
  margin-right: 0;
}
.field-name {
  font-weight: 600;
  margin-bottom: 2px;
}
.embed-image img {
  width: 100%;
  margin-top: 8px;
  border-radius: 4px;
}
.embed-thumbnail img {
  width: 80px;
  height: 80px;
  object-fit: cover;
  margin-left: 10px;
}
.embed-footer {
  margin-top: 8px;
  font-size: 12px;
  color: #b9bbbe;
  display: flex;
  align-items: center;
}
.footer-icon img {
  width: 20px;
  height: 20px;
  margin-right: 6px;
  border-radius: 50%;
}
</style>

