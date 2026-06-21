import express from 'express';
import { createServer } from 'http';
import { Server } from 'socket.io';
import { SiteManager } from './siteManager';
import { setupSocketHandler } from './socketHandler';
import path from 'path';

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

// Serve static WebRTC client files from public folder
app.use(express.static(path.join(process.cwd(), 'public')));

app.get('/download/master', (req, res) => {
  const password = req.query.password || req.query.sifre;
  if (password === 'adnanbey') {
    return res.download(path.join(process.cwd(), 'private', 'MasterUI.zip'), 'MasterUI.zip');
  } else {
    return res.status(403).send('Hatalı Şifre / Unauthorized');
  }
});

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

app.get('/api/chat/status', (req, res) => {
  res.json({ 
    enabled: siteManager.isChatSystemEnabled(),
    iceServers: siteManager.getIceServersConfig()
  });
});

setupSocketHandler(io, siteManager, AUTH_TOKEN);

server.listen(PORT, () => {
  console.log(`[Server] Baris Technical Service Suite Orchestrator running on port ${PORT}`);
});
