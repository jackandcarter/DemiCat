<template>
  <div v-if="settings.requests" class="requests">
    <h2>Request Board</h2>
    <ul>
      <li v-for="r in requests" :key="r.id">
        {{ r.title || r.id }}
      </li>
    </ul>
  </div>
  <p v-else>Feature disabled</p>
</template>

<script>
import settings from '../utils/settings';

export default {
  name: 'RequestsPage',
  data() {
    return { requests: [], ws: null, settings };
  },
  async created() {
    if (!this.settings.requests) return;
    await this.load();
    this.connect();
  },
  beforeUnmount() {
    if (this.ws) this.ws.close();
  },
  methods: {
    async load() {
      if (!this.settings.requests) return;
      try {
        const res = await fetch('/api/requests');
        if (res.ok) {
          this.requests = await res.json();
        }
      } catch (e) {
        console.error('Failed to load requests', e);
      }
    },
    connect() {
      if (!this.settings.requests) return;
      const proto = window.location.protocol === 'https:' ? 'wss' : 'ws';
      const url = `${proto}://${window.location.host}/ws/requests`;
      this.ws = new WebSocket(url);
      this.ws.onmessage = (ev) => {
        try {
          const req = JSON.parse(ev.data);
          const idx = this.requests.findIndex((r) => r.id === req.id);
          if (idx >= 0) this.requests.splice(idx, 1, req);
          else this.requests.unshift(req);
        } catch (e) {
          console.error('Bad request payload', e);
        }
      };
    }
  }
};
</script>

<style scoped>
.requests {
  padding: 1rem;
}
ul {
  list-style: none;
  padding: 0;
}
li {
  margin-bottom: 0.5rem;
}
</style>

