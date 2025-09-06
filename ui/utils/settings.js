import { reactive } from 'vue';

const defaults = {
  syncedChat: true,
  events: true,
  templates: true,
  requests: true,
  officer: true,
  fcSyncShell: false
};

const settings = reactive({ ...defaults });

fetch('/api/settings')
  .then(r => r.json())
  .then(data => Object.assign(settings, data))
  .catch(() => {});

export default settings;
