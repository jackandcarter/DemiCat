import { ref, watch } from 'vue';

const cache = { channels: null };

export function useEventChannels(tab) {
  const channels = ref([]);
  const selected = ref(localStorage.getItem(`eventChannel:${tab}`) || '');

  async function load() {
    if (!cache.channels) {
      try {
        const res = await fetch('/api/channels?kind=event');
        cache.channels = res.ok ? await res.json() : [];
      } catch (e) {
        console.error('Failed to load event channels', e);
        cache.channels = [];
      }
    }
    channels.value = cache.channels;
  }

  load();

  watch(selected, (val) => {
    localStorage.setItem(`eventChannel:${tab}`, val);
  });

  return { channels, selected };
}

