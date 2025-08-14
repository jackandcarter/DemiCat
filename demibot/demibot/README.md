
# demibot-fixed

A minimal, in-memory FastAPI backend that matches the DemiCat plugin contract.

## Run

```bash
python -m demibot.main
```

Environment variables (optional):

- DEMIBOT_HOST (default: 0.0.0.0)
- DEMIBOT_PORT (default: 8000)
- DEMIBOT_WS_PATH (default: /ws)
- DEMIBOT_API_KEY (default: demo)  # Use this same key in the plugin Settings → Sync
```

Endpoints implemented:
- POST /validate
- POST /roles
- GET /api/channels
- GET/POST /api/messages, /api/messages/{channelId}
- GET/POST /api/officer-messages, /api/officer-messages/{channelId}
- GET /api/users
- GET /api/embeds
- POST /api/events
- POST /api/interactions
- WebSocket at /ws
