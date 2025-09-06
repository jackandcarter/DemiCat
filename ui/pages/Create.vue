<template>
  <div class="create">
    <h2>Create Event</h2>
    <form @submit.prevent="submit">
      <div>
        <label>Channel ID: <input v-model="form.channelId" required /></label>
      </div>
      <div>
        <label>Title: <input v-model="form.title" required /></label>
      </div>
      <div>
        <label>Description:</label>
        <textarea v-model="form.description"></textarea>
      </div>
      <button type="submit">Create</button>
    </form>
    <div v-if="error" class="error">{{ error }}</div>
    <div v-if="created">
      <h3>Preview</h3>
      <EmbedRenderer :embed="created" />
    </div>
  </div>
</template>

<script>
import EmbedRenderer from '../components/EmbedRenderer.vue';

export default {
  name: 'CreatePage',
  components: { EmbedRenderer },
  data() {
    return {
      form: {
        channelId: '',
        title: '',
        description: ''
      },
      created: null,
      error: null
    };
  },
  methods: {
    validate() {
      if (this.form.title.length > 256) {
        this.error = 'Title too long';
        return false;
      }
      if (this.form.description.length > 4096) {
        this.error = 'Description too long';
        return false;
      }
      this.error = null;
      return true;
    },
    async submit() {
      if (!this.validate()) return;
      try {
        const res = await fetch('/api/events', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            channelId: this.form.channelId,
            title: this.form.title,
            description: this.form.description,
            time: new Date().toISOString()
          })
        });
        if (res.ok) {
          this.created = await res.json();
        }
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
form div {
  margin-bottom: 0.5rem;
}
textarea {
  width: 100%;
  height: 80px;
}
.error {
  color: red;
  margin-top: 0.5rem;
}
</style>

