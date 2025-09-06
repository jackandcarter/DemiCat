<template>
  <div v-if="settings.events" class="events">
    <div v-for="event in events" :key="event.id" class="event">
      <div class="attachments" v-if="event.attachments">
        <div v-for="(att, i) in event.attachments" :key="i">
          <img
            v-if="att.contentType && att.contentType.startsWith('image/')"
            :src="att.url"
            :alt="att.filename"
          />
          <a v-else :href="att.url" target="_blank">{{ att.filename }}</a>
        </div>
      </div>
      <div v-if="event.embeds">
        <EmbedRenderer v-for="(emb, i) in event.embeds" :key="i" :embed="emb" :event-id="event.id" />
      </div>
    </div>
  </div>
  <p v-else>Feature disabled</p>
</template>

<script>
import EmbedRenderer from '../components/EmbedRenderer.vue';
import settings from '../utils/settings';

export default {
  name: 'EventsPage',
  components: { EmbedRenderer },
  data() {
    return {
      events: [],
      settings
    };
  },
  async created() {
    if (!this.settings.events) return;
    try {
      const res = await fetch('/api/events');
      if (res.ok) {
        this.events = await res.json();
      }
    } catch (e) {
      console.error('Failed to load events', e);
    }
  }
};
</script>

<style scoped>
.events {
  padding: 1rem;
}
.event {
  margin-bottom: 1.5rem;
}
.attachments img {
  max-width: 200px;
  display: block;
  margin-bottom: 0.5rem;
}
</style>
