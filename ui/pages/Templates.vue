<template>
  <div v-if="settings.templates" class="templates">
    <h2>Templates</h2>
    <select v-model="selectedChannel">
      <option v-for="c in channels" :key="c.id" :value="c.id">{{ c.name }}</option>
    </select>
    <div v-for="t in templates" :key="t.id" class="template">
      <input v-model="t.name" @input="scheduleUpdate(t)" />
      <input v-model="t.description" @input="scheduleUpdate(t)" />
      <button @click="post(t.id)">Post</button>
      <button @click="remove(t.id)">Delete</button>
    </div>
  </div>
  <p v-else>Feature disabled</p>
</template>

<script>
import EmbedRenderer from '../components/EmbedRenderer.vue';

import { useEventChannels } from '../utils/useEventChannels.js';


export default {
  name: 'TemplatesPage',
  components: { EmbedRenderer },
  setup() {
    const { channels, selected } = useEventChannels('templates');
    return { channels, selectedChannel: selected };
  },
  data() {
    return { templates: [], ws: null, settings, patchTimers: {} };
  },
  async created() {
    if (!this.settings.templates) return;
    await this.load();
    this.connect();
  },
  beforeUnmount() {
    if (this.ws) this.ws.close();
  },
  methods: {
    async load() {
      if (!this.settings.templates) return;
      try {
        const res = await fetch('/api/templates');
        if (res.ok) {
          const data = await res.json();
          this.templates.splice(0, this.templates.length, ...data);
          this.sortTemplates();
        }
      } catch (e) {
        console.error('Failed to load templates', e);
      }
    },
    connect() {
      if (!this.settings.templates) return;
      const proto = window.location.protocol === 'https:' ? 'wss' : 'ws';
      const url = `${proto}://${window.location.host}/ws/templates`;
      this.ws = new WebSocket(url);
      this.ws.onmessage = (ev) => {
        try {
          const msg = JSON.parse(ev.data);
          if (msg.topic === 'templates.updated') {
            const p = msg.payload || {};
            if (p.deleted) {
              const idx = this.templates.findIndex(t => t.id === p.id);
              if (idx >= 0) this.templates.splice(idx, 1);
            } else if (p.id) {
              const idx = this.templates.findIndex(t => t.id === p.id);
              if (idx >= 0) {
                Object.assign(this.templates[idx], p);
              } else {
                this.templates.push(p);
              }
              this.sortTemplates();
            } else {
              this.load();
            }
          }
        } catch (e) {
          console.error('Bad embed payload', e);
        }
      };
    },
    async post(id) {

      const tmpl = this.templates.find(t => t.id === id);
      if (!tmpl) return;
      const body = Object.assign({}, tmpl.payload, { channelId: this.selectedChannel });
      try {
        await fetch('/api/events', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body)
        });
      } catch (e) {
        console.error('Failed to post template', e);
      }
    },
    async remove(id) {
      if (this.settings.templates)
        await fetch(`/api/templates/${id}`, { method: 'DELETE' });
    },
    sortTemplates() {
      this.templates.sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt));
    },
    scheduleUpdate(t) {
      if (this.patchTimers[t.id]) clearTimeout(this.patchTimers[t.id]);
      this.patchTimers[t.id] = setTimeout(() => this.save(t), 500);
    },
    async save(t) {
      try {
        await fetch(`/api/templates/${t.id}`, {
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ name: t.name, description: t.description })
        });
      } catch (e) {
        console.error('Failed to update template', e);
      }
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

