<template>
  <div id="app">
    <nav class="nav">
      <router-link v-if="settings.events" to="/events">Events</router-link>
      <router-link v-if="settings.events" to="/create">Create</router-link>
      <router-link v-if="settings.templates" to="/templates">Templates</router-link>
      <router-link v-if="settings.requests" to="/requests">Requests</router-link>
      <router-link v-if="settings.syncedChat" to="/chat">Chat</router-link>
      <router-link v-if="settings.fcSyncShell" to="/syncshell">SyncShell</router-link>
      <router-link v-if="settings.officer" to="/officer">Officer</router-link>
    </nav>
    <router-view />
  </div>
</template>

<script>
import settings from './utils/settings';
export default {
  name: 'App',
  data() {
    return { settings };
  }
};
</script>

<style scoped>
.nav {
  display: flex;
  gap: 1rem;
  padding: 1rem;
}
.nav a {
  text-decoration: none;
}
</style>

