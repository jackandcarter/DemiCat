<template>
  <div class="message">
    <div v-if="message.content" class="content">{{ message.content }}</div>
    <!-- Webhook messages (type 20) use the same renderer -->
    <EmbedRenderer v-for="(embed, i) in message.embeds" :key="i" :embed="embed" />
  </div>
</template>

<script>
import EmbedRenderer from './EmbedRenderer.vue';

export default {
  name: 'Message',
  components: { EmbedRenderer },
  props: {
    message: {
      type: Object,
      required: true
    }
  },
  computed: {
    isWebhook() {
      return this.message.type === 20;
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
</style>

