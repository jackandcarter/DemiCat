<template>
  <div class="syncshell">
    <h2>SyncShell</h2>
    <div class="controls">
      <input v-model="apiKey" placeholder="API Key" />
      <input v-model="channelId" placeholder="Channel ID" />
      <button @click="setup">Connect</button>
    </div>
    <div class="messages">
      <Message v-for="m in messages" :key="m.id" :message="m" />
    </div>
  </div>
</template>

<script>
import Message from '../components/Message.vue';

export default {
  name: 'SyncShellPage',
  components: { Message },
  data() {
    return { apiKey: '', channelId: '', messages: [], ws: null };
  },
  beforeUnmount() {
    if (this.ws) this.ws.close();
  },
  methods: {
    async setup() {
      if (!this.channelId) return;
      await this.load();
      this.connect();
    },
    async load() {
      try {
        const res = await fetch(`/api/messages/${this.channelId}`);
        if (res.ok) {
          this.messages = await res.json();
        }
      } catch (e) {
        console.error('Failed to load messages', e);
      }
    },
    connect() {
      if (this.ws) {
        this.ws.close();
        this.ws = null;
      }
      const proto = window.location.protocol === 'https:' ? 'wss' : 'ws';
      let url = `${proto}://${window.location.host}/ws/syncshell`;
      if (this.apiKey) {
        url += `?token=${encodeURIComponent(this.apiKey)}`;
      }
      this.ws = new WebSocket(url);
      this.ws.onmessage = (ev) => {
        try {
          const msg = JSON.parse(ev.data);
          if (msg.channelId === this.channelId) {
            this.messages.push(msg);
          }
        } catch (e) {
          console.error('Bad syncshell payload', e);
        }
      };
    }
  }
};
</script>

<style scoped>
.syncshell {
  padding: 1rem;
}
.controls input {
  margin-right: 0.5rem;
}
.messages {
  margin-top: 1rem;
}
</style>

