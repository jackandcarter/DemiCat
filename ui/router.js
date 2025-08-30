import { createRouter, createWebHistory } from 'vue-router';
import Events from './pages/Events.vue';
import Create from './pages/Create.vue';
import Templates from './pages/Templates.vue';
import Requests from './pages/Requests.vue';
import Chat from './pages/Chat.vue';
import SyncShell from './pages/SyncShell.vue';
import Officer from './pages/Officer.vue';

const routes = [
  { path: '/', redirect: '/events' },
  { path: '/events', component: Events },
  { path: '/create', component: Create },
  { path: '/templates', component: Templates },
  { path: '/requests', component: Requests },
  { path: '/chat', component: Chat },
  { path: '/syncshell', component: SyncShell },
  { path: '/officer', component: Officer }
];

const router = createRouter({
  history: createWebHistory(),
  routes
});

export default router;
