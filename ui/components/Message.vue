<template>
  <div class="message">
    <div v-if="isEditing">
      <textarea v-model="editContent" class="edit-box"></textarea>
      <button @click="saveEdit">Save</button>
      <button @click="cancelEdit">Cancel</button>
    </div>
    <div v-else>
      <div v-if="message.content" class="content">{{ message.content }}</div>
      <div class="reference" v-if="message.reference">
        Replying to {{ message.reference.messageId || message.reference.id }}
      </div>
      <div class="attachments" v-if="message.attachments">
        <div v-for="(att, i) in message.attachments" :key="i">
          <img v-if="att.contentType && att.contentType.startsWith('image/')" :src="att.url" :alt="att.filename" />
          <a v-else :href="att.url" target="_blank">{{ att.filename }}</a>
        </div>
      </div>
      <!-- Webhook messages (type 20) use the same renderer -->
      <EmbedRenderer v-for="(embed, i) in message.embeds" :key="i" :embed="embed" />
      <div class="controls" v-if="isAuthor">
        <button @click="startEdit">Edit</button>
        <button @click="deleteMessage">Delete</button>
      </div>
      <div class="reactions">
        <button v-for="e in emojis" :key="e" @click="react(e)">{{ e }}</button>
      </div>
    </div>
  </div>
</template>

<script>
import EmbedRenderer from './EmbedRenderer.vue';

export default {
  name: 'Message',
  components: { EmbedRenderer },
  props: {
    message: { type: Object, required: true },
    currentUserId: { type: String, required: false }
  },
  data() {
    return {
      isEditing: false,
      editContent: this.message.content,
      emojis: ['👍', '👎', '❤️']
    };
  },
  computed: {
    isAuthor() {
      return (
        this.currentUserId &&
        this.message.author &&
        this.message.author.id === this.currentUserId
      );
    }
  },
  methods: {
    react(emoji) {
      fetch(
        `/api/channels/${this.message.channelId}/messages/${this.message.id}/reactions/${encodeURIComponent(emoji)}`,
        { method: 'PUT' }
      );
    },
    startEdit() {
      this.isEditing = true;
      this.editContent = this.message.content;
    },
    cancelEdit() {
      this.isEditing = false;
    },
    saveEdit() {
      fetch(`/api/channels/${this.message.channelId}/messages/${this.message.id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ content: this.editContent })
      });
      this.isEditing = false;
    },
    deleteMessage() {
      fetch(`/api/channels/${this.message.channelId}/messages/${this.message.id}`, {
        method: 'DELETE'
      });
    }
  }
};
</script>

<style scoped>
.message {
  margin-bottom: 1rem;
}
.content {
  margin-bottom: 0.5rem;
}
.attachments img {
  max-width: 200px;
  display: block;
}
.reactions button {
  margin-right: 0.25rem;
}
</style>

