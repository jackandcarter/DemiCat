<template>
  <div id="app">
    <nav class="nav">
      <router-link to="/events">Events</router-link>
      <router-link to="/create">Create</router-link>
      <router-link to="/templates">Templates</router-link>
      <router-link to="/requests">Requests</router-link>
      <router-link to="/chat">Chat</router-link>
      <router-link to="/syncshell">SyncShell</router-link>
      <router-link to="/officer">Officer</router-link>
    </nav>
    <router-view />
  </div>
</template>

<script>
export default {
  name: 'App'
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

