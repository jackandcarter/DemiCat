<template>
  <div class="create">
    <h2>Create Event</h2>
    <div class="pane">
      <div class="editor">
        <div>
          <select v-model="selectedChannel">
            <option v-for="c in channels" :key="c.id" :value="c.id">{{ c.name }}</option>
          </select>
        </div>
        <textarea v-model="raw"></textarea>
        <div v-if="error" class="error">{{ error }}</div>
        <button @click="submit" :disabled="!!error">Create</button>
      </div>
      <div class="preview" v-if="preview">
        <EmbedRenderer :embed="preview" />
      </div>
    </div>
  </div>
</template>

<script>
import EmbedRenderer from '../components/EmbedRenderer.vue';
import { validateEmbed } from '../utils/embed.js';
import { useEventChannels } from '../utils/useEventChannels.js';

export default {
  name: 'CreatePage',
  components: { EmbedRenderer },
  setup() {
    const { channels, selected } = useEventChannels('create');
    return { channels, selectedChannel: selected };
  },
  data() {
    return {
      raw: '{"title":"","description":""}'
    };
  },
  computed: {
    preview() {
      try {
        return JSON.parse(this.raw);
      } catch (e) {
        return null;
      }
    },
    error() {
      if (!this.preview) return 'Invalid JSON';
      return validateEmbed(this.preview, this.preview.buttons || []);
    }
  },
  methods: {
    async submit() {
      if (this.error) return;
      const payload = Object.assign({ channelId: this.selectedChannel }, this.preview);
      try {
        await fetch('/api/events', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload)
        });
      } catch (e) {
        console.error('Failed to create event', e);
      }
    }
  }
};
</script>

<style scoped>
.create {
  padding: 1rem;
}
.pane {
  display: flex;
  gap: 1rem;
}
.editor, .preview {
  flex: 1;
}
textarea {
  width: 100%;
  height: 300px;
}
.error {
  color: red;
  margin-top: 0.5rem;
}
</style>
