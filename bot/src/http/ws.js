const WebSocket = require('ws');

let messageHub;
let embedHub;

function heartbeat(wss) {
  wss.on('connection', ws => {
    ws.isAlive = true;
    ws.on('pong', () => {
      ws.isAlive = true;
    });
  });

  const interval = setInterval(() => {
    wss.clients.forEach(ws => {
      if (!ws.isAlive) return ws.terminate();
      ws.isAlive = false;
      ws.ping();
    });
  }, 30000);

  wss.on('close', () => clearInterval(interval));
}

function broadcastMessage(msg) {
  if (!messageHub) return;
  const data = JSON.stringify(msg);
  messageHub.clients.forEach(ws => {
    if (ws.readyState === WebSocket.OPEN) {
      ws.send(data);
    }
  });
}

function broadcastEmbed(embed) {
  if (!embedHub) return;
  const data = JSON.stringify(embed);
  embedHub.clients.forEach(ws => {
    if (ws.readyState === WebSocket.OPEN) {
      ws.send(data);
    }
  });
}

function start(server, discord, logger) {
  messageHub = new WebSocket.Server({ noServer: true });
  embedHub = new WebSocket.Server({ noServer: true });

  heartbeat(messageHub);
  heartbeat(embedHub);

  server.on('upgrade', (req, socket, head) => {
    if (req.url === '/ws/messages') {
      messageHub.handleUpgrade(req, socket, head, ws => {
        messageHub.emit('connection', ws, req);
        for (const arr of discord.messageCache.values()) {
          for (const msg of arr) {
            ws.send(JSON.stringify(msg));
          }
        }
      });
    } else if (req.url === '/ws/embeds') {
      embedHub.handleUpgrade(req, socket, head, ws => {
        embedHub.emit('connection', ws, req);
        discord.embedCache.forEach(e => ws.send(JSON.stringify(e)));
      });
    } else {
      socket.destroy();
    }
  });

  if (logger) logger.info('WebSocket hubs configured');
}

module.exports = { start, broadcastMessage, broadcastEmbed };
