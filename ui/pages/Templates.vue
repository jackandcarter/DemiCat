<template>
  <div class="templates">
    <h2>Templates</h2>
    <div v-for="(t, i) in templates" :key="i" class="template">
      <EmbedRenderer :embed="t" />
    </div>
  </div>
</template>

<script>
import EmbedRenderer from '../components/EmbedRenderer.vue';

export default {
  name: 'TemplatesPage',
  components: { EmbedRenderer },
  data() {
    return { templates: [], ws: null };
  },
  async created() {
    await this.load();
    this.connect();
  },
  beforeUnmount() {
    if (this.ws) this.ws.close();
  },
  methods: {
    async load() {
      try {
        const res = await fetch('/api/embeds');
        if (res.ok) {
          this.templates = await res.json();
        }
      } catch (e) {
        console.error('Failed to load templates', e);
      }
    },
    connect() {
      const proto = window.location.protocol === 'https:' ? 'wss' : 'ws';
      const url = `${proto}://${window.location.host}/ws/embeds`;
      this.ws = new WebSocket(url);
      this.ws.onmessage = (ev) => {
        try {
          const emb = JSON.parse(ev.data);
          this.templates.unshift(emb);
        } catch (e) {
          console.error('Bad embed payload', e);
        }
      };
    }
  }
};
</script>

<style scoped>
.templates {
  padding: 1rem;
}
.template {
  margin-bottom: 1rem;
}
</style>

