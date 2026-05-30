import express from 'express';
import { createServer } from 'http';
import { Server } from 'socket.io';
import { SiteManager } from './siteManager';
import { setupSocketHandler } from './socketHandler';

const app = express();
const server = createServer(app);
const io = new Server(server, {
  cors: {
    origin: '*',
    methods: ['GET', 'POST']
  },
  maxHttpBufferSize: 1e7 // 10MB limit for transferring binary PLC data/payloads
});

const siteManager = new SiteManager();
const PORT = process.env.PORT || 3000;
const AUTH_TOKEN = process.env.AUTH_TOKEN || 'BarisServis2026!';

app.get('/health', (req, res) => {
  res.json({ status: 'OK', sitesCount: siteManager.getSitesList().length });
});

app.get('/api/sites', (req, res) => {
  const token = req.headers['x-auth-token'];
  if (token !== AUTH_TOKEN) {
    return res.status(401).json({ error: 'Unauthorized' });
  }
  res.json(siteManager.getSitesList());
});

setupSocketHandler(io, siteManager, AUTH_TOKEN);

server.listen(PORT, () => {
  console.log(`[Server] Baris Technical Service Suite Orchestrator running on port ${PORT}`);
});
